/*++

Copyright (c) Microsoft Corporation, All Rights Reserved

Module Name:

    Queue.c

Abstract:

    This file implements the I/O queue interface and performs
    the read/write/ioctl operations.

Environment:

    Windows Driver Framework

--*/


#include "internal.h"
#include "simulator-protocol.h"

#define SIM_CTS_HANDSHAKE 0x08u
#define SIM_TX_WAITING_FOR_CTS 0x01u
#define SIM_SERIAL_DTR_CONTROL 0x0001u
#define SIM_SERIAL_RTS_CONTROL 0x0002u
typedef struct _SIM_HANDFLOW {
    ULONG ControlHandShake;
    ULONG FlowReplace;
    LONG XonLimit;
    LONG XoffLimit;
} SIM_HANDFLOW;

static VOID
QueueSignalRxChar(
    _In_ PQUEUE_CONTEXT QueueContext
    );

static VOID QueuePumpLine(_In_ PQUEUE_CONTEXT QueueContext);

static VOID
QueueRemovePendingReadLocked(
    _In_ PQUEUE_CONTEXT QueueContext,
    _In_ WDFREQUEST Request
    )
{
    ULONG index;
    for (index = 0; index < QueueContext->PendingReadCount; index++) {
        if (QueueContext->PendingReads[index] == Request) {
            ULONG move;
            for (move = index + 1; move < QueueContext->PendingReadCount; move++) {
                QueueContext->PendingReads[move - 1] = QueueContext->PendingReads[move];
            }
            QueueContext->PendingReadCount--;
            return;
        }
    }
}

static WDFREQUEST
QueueTakePendingRead(
    _In_ PQUEUE_CONTEXT QueueContext
    )
{
    WDFREQUEST request = NULL;
    ULONG index;

    WdfWaitLockAcquire(QueueContext->PendingReadsLock, NULL);
    if (QueueContext->PendingReadCount > 0) {
        request = QueueContext->PendingReads[0];
        for (index = 1; index < QueueContext->PendingReadCount; index++) {
            QueueContext->PendingReads[index - 1] = QueueContext->PendingReads[index];
        }
        QueueContext->PendingReadCount--;
    }
    WdfWaitLockRelease(QueueContext->PendingReadsLock);
    return request;
}

static VOID
QueueEvtReadCancel(
    _In_ WDFREQUEST Request
    )
{
    WDFQUEUE queue = WdfRequestGetIoQueue(Request);
    if (queue != NULL) {
        PQUEUE_CONTEXT queueContext = GetQueueContext(queue);
        WdfWaitLockAcquire(queueContext->PendingReadsLock, NULL);
        QueueRemovePendingReadLocked(queueContext, Request);
        WdfWaitLockRelease(queueContext->PendingReadsLock);
    }
    WdfRequestComplete(Request, STATUS_CANCELLED);
}

static VOID
QueueServePendingReads(_In_ PQUEUE_CONTEXT QueueContext)
{
    for (;;) {
        WDFREQUEST request;
        WDFMEMORY memory;
        PVOID buffer;
        size_t bufferSize = 0;
        size_t bytesCopied = 0;
        size_t available = 0;
        NTSTATUS status;

        RingBufferGetAvailableData(&QueueContext->RingBuffer, &available);
        if (available == 0) {
            break;
        }

        request = QueueTakePendingRead(QueueContext);
        if (request == NULL) {
            break;
        }

        status = WdfRequestUnmarkCancelable(request);
        if (!NT_SUCCESS(status)) {
            // Canceled in flight: QueueEvtReadCancel already completed it.
            continue;
        }

        status = WdfRequestRetrieveOutputMemory(request, &memory);
        if (!NT_SUCCESS(status)) {
            WdfRequestComplete(request, status);
            continue;
        }

        buffer = WdfMemoryGetBuffer(memory, &bufferSize);
        status = RingBufferRead(&QueueContext->RingBuffer, buffer, bufferSize, &bytesCopied);
        if (!NT_SUCCESS(status)) {
            WdfRequestComplete(request, status);
            continue;
        }

        WdfRequestCompleteWithInformation(request, STATUS_SUCCESS, bytesCopied);
        if (bytesCopied == 0) {
            break;
        }
    }

    QueueSignalRxChar(QueueContext);
}

static VOID
QueueWakeReaders(_In_ PQUEUE_CONTEXT QueueContext)
{
    QueueServePendingReads(QueueContext);
}

// Credits use half-bit milliseconds: baud * elapsed_ms * 2.
// A byte costs (start + data + parity + stop) * 2000 units.
static size_t
QueueLineAllowance(_In_ PQUEUE_CONTEXT Context, _Inout_ ULONGLONG *Credit,
    _In_ size_t Pending, _In_ size_t Space, _In_ ULONGLONG Elapsed)
{
    PDEVICE_CONTEXT device = Context->DeviceContext;
    ULONGLONG halfBits = 2ULL * (1 + device->SerialDataBits +
        (device->SerialParity != 0)) + (device->SerialStopBits == 1 ? 3 :
        device->SerialStopBits == 2 ? 4 : 2);
    ULONGLONG cost = halfBits * 1000;
    size_t count;
    if (Pending == 0) { *Credit = 0; return 0; }
    if ((device->SerialFlowControl & 1) == 0) return min(min(Pending, Space), 4096);
    // Do not accrue a burst while the destination queue is full.
    if (Space == 0) return 0;
    *Credit += (ULONGLONG)device->BaudRate * Elapsed * 2;
    count = (size_t)min(*Credit / cost, 4096);
    return min(min(count, Pending), Space);
}

static VOID
QueueMoveBytes(_In_ PRING_BUFFER From, _In_ PRING_BUFFER To,
    _In_ size_t Count)
{
    BYTE bytes[4096];
    size_t copied = 0;
    if (Count == 0) return;
    if (NT_SUCCESS(RingBufferRead(From, bytes, Count, &copied)) && copied != 0)
        RingBufferWrite(To, bytes, copied);
}

static VOID
QueuePumpLine(_In_ PQUEUE_CONTEXT Context)
{
    ULONGLONG now = GetTickCount64();
    ULONGLONG elapsed = now - Context->LastLineTick;
    ULONGLONG halfBits = 2ULL * (1 + Context->DeviceContext->SerialDataBits +
        (Context->DeviceContext->SerialParity != 0)) +
        (Context->DeviceContext->SerialStopBits == 1 ? 3 :
        Context->DeviceContext->SerialStopBits == 2 ? 4 : 2);
    size_t pending, space, rxCount, txCount;
    if (elapsed > 1000) elapsed = 1000;
    Context->LastLineTick = now;

    RingBufferGetAvailableData(&Context->InFlightRx, &pending);
    RingBufferGetAvailableSpace(&Context->RingBuffer, &space);
    rxCount = QueueLineAllowance(Context, &Context->RxCredit, pending, space, elapsed);
    QueueMoveBytes(&Context->InFlightRx, &Context->RingBuffer, rxCount);
    if ((Context->DeviceContext->SerialFlowControl & 1) != 0)
        Context->RxCredit -= rxCount * halfBits * 1000;
    if (rxCount != 0) QueueWakeReaders(Context);

    RingBufferGetAvailableData(&Context->InFlightTx, &pending);
    RingBufferGetAvailableSpace(&Context->BusinessRequests, &space);
    if ((Context->HandflowControl & SIM_CTS_HANDSHAKE) != 0 &&
        (Context->ModemStatus & MS_CTS_ON) == 0) {
        Context->TxCredit = 0;
        txCount = 0;
    } else {
        txCount = QueueLineAllowance(Context, &Context->TxCredit, pending, space, elapsed);
    }
    QueueMoveBytes(&Context->InFlightTx, &Context->BusinessRequests, txCount);
    if ((Context->DeviceContext->SerialFlowControl & 1) != 0)
        Context->TxCredit -= txCount * halfBits * 1000;
}

VOID EvtLineTimer(_In_ WDFTIMER Timer)
{
    QueuePumpLine(GetQueueContext((WDFQUEUE)WdfTimerGetParentObject(Timer)));
    WdfTimerStart(Timer, WDF_REL_TIMEOUT_IN_MS(10));
}

NTSTATUS
QueueCreate(
    _In_  PDEVICE_CONTEXT   DeviceContext
    )
{
    NTSTATUS                status;
    WDFDEVICE               device = DeviceContext->Device;
    WDF_IO_QUEUE_CONFIG     queueConfig;
    WDF_OBJECT_ATTRIBUTES   queueAttributes;
    WDF_OBJECT_ATTRIBUTES   lockAttributes;
    WDF_TIMER_CONFIG        timerConfig;
    WDF_OBJECT_ATTRIBUTES   timerAttributes;
    WDFQUEUE                queue;
    PQUEUE_CONTEXT          queueContext;

    //
    // Create the default queue
    //

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(
                            &queueConfig,
                            WdfIoQueueDispatchParallel);

    queueConfig.EvtIoRead           = EvtIoRead;
    queueConfig.EvtIoWrite          = EvtIoWrite;
    queueConfig.EvtIoDeviceControl  = EvtIoDeviceControl;

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(
                            &queueAttributes,
                            QUEUE_CONTEXT);

    status = WdfIoQueueCreate(
                            device,
                            &queueConfig,
                            &queueAttributes,
                            &queue);

    if( !NT_SUCCESS(status) ) {
        Trace(TRACE_LEVEL_ERROR,
            "Error: WdfIoQueueCreate failed 0x%x", status);
        return status;
    }

    queueContext = GetQueueContext(queue);
    queueContext->Queue = queue;
    queueContext->DeviceContext = DeviceContext;
    queueContext->ModemStatus = MS_CTS_ON | MS_DSR_ON | MS_RLSD_ON;

    //
    // Create a manual queue to hold pending read requests. By keeping
    // them in the queue, framework takes care of cancelling them if the app
    // exits
    //

    WDF_IO_QUEUE_CONFIG_INIT(
                            &queueConfig,
                            WdfIoQueueDispatchManual);

    status = WdfIoQueueCreate(
                            device,
                            &queueConfig,
                            WDF_NO_OBJECT_ATTRIBUTES,
                            &queue);

    if( !NT_SUCCESS(status) ) {
        Trace(TRACE_LEVEL_ERROR,
            "Error: WdfIoQueueCreate manual queue failed 0x%x", status);
        return status;
    }

    queueContext->ReadQueue = queue;

    //
    // Create another manual queue to hold pending IOCTL_SERIAL_WAIT_ON_MASK
    //

    WDF_IO_QUEUE_CONFIG_INIT(
                            &queueConfig,
                            WdfIoQueueDispatchManual);

    status = WdfIoQueueCreate(
                            device,
                            &queueConfig,
                            WDF_NO_OBJECT_ATTRIBUTES,
                            &queue);

    if( !NT_SUCCESS(status) ) {
        Trace(TRACE_LEVEL_ERROR,
            "Error: WdfIoQueueCreate manual queue failed 0x%x", status);
        return status;
    }

    queueContext->WaitMaskQueue = queue;

    queueContext->PendingReadCount = 0;
    WDF_OBJECT_ATTRIBUTES_INIT(&lockAttributes);
    lockAttributes.ParentObject = queueContext->Queue;
    status = WdfWaitLockCreate(&lockAttributes, &queueContext->PendingReadsLock);
    if (!NT_SUCCESS(status)) {
        Trace(TRACE_LEVEL_ERROR,
            "Error: WdfWaitLockCreate failed 0x%x", status);
        return status;
    }

    RingBufferInitialize(&queueContext->RingBuffer,
                            queueContext->Buffer,
                            sizeof(queueContext->Buffer));
    RingBufferInitialize(&queueContext->InFlightRx,
                            queueContext->InFlightRxBuffer,
                            sizeof(queueContext->InFlightRxBuffer));
    RingBufferInitialize(&queueContext->BusinessRequests,
                            queueContext->BusinessBuffer,
                            sizeof(queueContext->BusinessBuffer));
    RingBufferInitialize(&queueContext->InFlightTx,
                            queueContext->InFlightTxBuffer,
                            sizeof(queueContext->InFlightTxBuffer));
    queueContext->LastLineTick = GetTickCount64();
    // UMDF callbacks run at PASSIVE_LEVEL. Passive WDF timers must be
    // one-shot, so rearm in EvtLineTimer instead of using a periodic timer.
    WDF_TIMER_CONFIG_INIT(&timerConfig, EvtLineTimer);
    WDF_OBJECT_ATTRIBUTES_INIT(&timerAttributes);
    timerAttributes.ParentObject = queueContext->Queue;
    timerAttributes.ExecutionLevel = WdfExecutionLevelPassive;
    status = WdfTimerCreate(&timerConfig, &timerAttributes, &queueContext->LineTimer);
    if (NT_SUCCESS(status)) WdfTimerStart(queueContext->LineTimer, WDF_REL_TIMEOUT_IN_MS(10));
    else Trace(TRACE_LEVEL_ERROR, "WdfTimerCreate failed 0x%x", status);

    return status;
}


NTSTATUS
RequestCopyFromBuffer(
    _In_  WDFREQUEST        Request,
    _In_  PVOID             SourceBuffer,
    _In_  size_t            NumBytesToCopyFrom
    )
{
    NTSTATUS                status;
    WDFMEMORY               memory;

    status = WdfRequestRetrieveOutputMemory(Request, &memory);
    if( !NT_SUCCESS(status) ) {
        Trace(TRACE_LEVEL_ERROR,
            "Error: WdfRequestRetrieveOutputMemory failed 0x%x", status);
        return status;
    }

    status = WdfMemoryCopyFromBuffer(memory, 0,
                            SourceBuffer, NumBytesToCopyFrom);
    if( !NT_SUCCESS(status) ) {
        Trace(TRACE_LEVEL_ERROR,
            "Error: WdfMemoryCopyFromBuffer failed 0x%x", status);
        return status;
    }

    WdfRequestSetInformation(Request, NumBytesToCopyFrom);
    return status;
}


NTSTATUS
RequestCopyToBuffer(
    _In_  WDFREQUEST        Request,
    _In_  PVOID             DestinationBuffer,
    _In_  size_t            NumBytesToCopyTo
    )
{
    NTSTATUS                status;
    WDFMEMORY               memory;

    status = WdfRequestRetrieveInputMemory(Request, &memory);
    if( !NT_SUCCESS(status) ) {
        Trace(TRACE_LEVEL_ERROR,
            "Error: WdfRequestRetrieveInputMemory failed 0x%x", status);
        return status;
    }

    status = WdfMemoryCopyToBuffer(memory, 0,
                            DestinationBuffer, NumBytesToCopyTo);
    if( !NT_SUCCESS(status) ) {
        Trace(TRACE_LEVEL_ERROR,
            "Error: WdfMemoryCopyToBuffer failed 0x%x", status);
        return status;
    }

    return status;
}

static VOID
QueueSignalRxChar(
    _In_ PQUEUE_CONTEXT QueueContext
    )
{
    WDFREQUEST pending;
    ULONG eventMask = EV_RXCHAR;
    NTSTATUS status;

    if ((QueueContext->WaitMask & EV_RXCHAR) == 0) {
        return;
    }
    status = WdfIoQueueRetrieveNextRequest(QueueContext->WaitMaskQueue, &pending);
    if (!NT_SUCCESS(status)) {
        return;
    }
    status = RequestCopyFromBuffer(pending, &eventMask, sizeof(eventMask));
    WdfRequestCompleteWithInformation(
        pending,
        status,
        NT_SUCCESS(status) ? sizeof(eventMask) : 0);
}


VOID
EvtIoDeviceControl(
    _In_  WDFQUEUE          Queue,
    _In_  WDFREQUEST        Request,
    _In_  size_t            OutputBufferLength,
    _In_  size_t            InputBufferLength,
    _In_  ULONG             IoControlCode
    )
{
    NTSTATUS                status;
    PQUEUE_CONTEXT          queueContext = GetQueueContext(Queue);
    PDEVICE_CONTEXT         deviceContext = queueContext->DeviceContext;
    UNREFERENCED_PARAMETER  (OutputBufferLength);
    UNREFERENCED_PARAMETER  (InputBufferLength);

    Trace(TRACE_LEVEL_INFO,
        "EvtIoDeviceControl 0x%x", IoControlCode);

    switch (IoControlCode)
    {
    case IOCTL_SERIAL_GET_PROPERTIES:
    {
        COMMPROP properties = {0};
        properties.wPacketLength = sizeof(properties);
        properties.wPacketVersion = 2;
        properties.dwServiceMask = SP_SERIALCOMM;
        properties.dwMaxTxQueue = DATA_BUFFER_SIZE - 1;
        properties.dwMaxRxQueue = DATA_BUFFER_SIZE - 1;
        properties.dwCurrentRxQueue = DATA_BUFFER_SIZE - 1;
        properties.dwCurrentTxQueue = DATA_BUFFER_SIZE - 1;
        properties.dwMaxBaud = BAUD_USER;
        properties.dwProvSubType = PST_RS232;
        properties.dwProvCapabilities = PCF_TOTALTIMEOUTS | PCF_INTTIMEOUTS;
        properties.dwSettableParams = SP_BAUD | SP_DATABITS | SP_STOPBITS | SP_PARITY;
        properties.dwSettableBaud = BAUD_USER;
        properties.wSettableData = DATABITS_5 | DATABITS_6 | DATABITS_7 | DATABITS_8;
        properties.wSettableStopParity = STOPBITS_10 | STOPBITS_15 | STOPBITS_20 | PARITY_NONE | PARITY_ODD | PARITY_EVEN;
        status = RequestCopyFromBuffer(Request, &properties, sizeof(properties));
        break;
    }
    case IOCTL_SERIAL_GET_MODEMSTATUS:
    {
        ULONG modemStatus = queueContext->ModemStatus;
        status = RequestCopyFromBuffer(Request, &modemStatus, sizeof(modemStatus));
        break;
    }
    case IOCTL_SERIAL_GET_CHARS:
    {
        BYTE chars[6] = {0, 0, 0, 0, 17, 19};
        status = RequestCopyFromBuffer(Request, chars, sizeof(chars));
        break;
    }
    case IOCTL_SERIAL_GET_HANDFLOW:
    {
        SIM_HANDFLOW flow = {queueContext->HandflowControl,
            queueContext->HandflowReplace, queueContext->XonLimit,
            queueContext->XoffLimit};
        status = RequestCopyFromBuffer(Request, &flow, sizeof(flow));
        break;
    }
    case IOCTL_SERIAL_GET_COMMSTATUS:
    {
        struct { ULONG errors, holds, input, output; BYTE eof, waiting; } commStatus = {0};
        size_t bytes;
        RingBufferGetAvailableData(&queueContext->RingBuffer, &bytes);
        commStatus.input = (ULONG)bytes;
        RingBufferGetAvailableData(&queueContext->BusinessRequests, &bytes);
        commStatus.output = (ULONG)bytes;
        RingBufferGetAvailableData(&queueContext->InFlightTx, &bytes);
        commStatus.output += (ULONG)bytes;
        if ((queueContext->HandflowControl & SIM_CTS_HANDSHAKE) != 0 &&
            (queueContext->ModemStatus & MS_CTS_ON) == 0)
            commStatus.holds = SIM_TX_WAITING_FOR_CTS;
        status = RequestCopyFromBuffer(Request, &commStatus, sizeof(commStatus));
        break;
    }
    case IOCTL_SIM_GET_MODEM_LINES:
    {
        ULONG lines = queueContext->ModemStatus;
        status = RequestCopyFromBuffer(Request, &lines, sizeof(lines));
        WdfRequestCompleteWithInformation(Request, status, NT_SUCCESS(status) ? sizeof(lines) : 0);
        return;
    }
    case IOCTL_SIM_GET_LINE_STATE:
    {
        SIM_LINE_STATE state;
        state.ModemStatus = queueContext->ModemStatus;
        state.DtrOn = deviceContext->DtrOn ? 1 : 0;
        state.RtsOn = deviceContext->RtsOn ? 1 : 0;
        state.CtsHandshake =
            (queueContext->HandflowControl & SIM_CTS_HANDSHAKE) ? 1 : 0;
        status = RequestCopyFromBuffer(Request, &state, sizeof(state));
        WdfRequestCompleteWithInformation(Request, status, NT_SUCCESS(status) ? sizeof(state) : 0);
        return;
    }
    case IOCTL_SIM_SET_MODEM_LINES:
    {
        ULONG lines = 0;
        status = RequestCopyToBuffer(Request, &lines, sizeof(lines));
        if (NT_SUCCESS(status) && (InputBufferLength != sizeof(lines) ||
            (lines & ~(MS_CTS_ON | MS_DSR_ON | MS_RLSD_ON)) != 0))
            status = STATUS_INVALID_PARAMETER;
        if (NT_SUCCESS(status)) {
            queueContext->ModemStatus = lines;
            QueuePumpLine(queueContext);
        }
        WdfRequestCompleteWithInformation(Request, status, NT_SUCCESS(status) ? sizeof(lines) : 0);
        return;
    }
    case IOCTL_SERIAL_PURGE:
    {
        ULONG flags;
        status = RequestCopyToBuffer(Request, &flags, sizeof(flags));
        if (NT_SUCCESS(status)) {
            if (flags & PURGE_RXCLEAR) {
                RingBufferInitialize(&queueContext->RingBuffer, queueContext->Buffer, sizeof(queueContext->Buffer));
                RingBufferInitialize(&queueContext->InFlightRx, queueContext->InFlightRxBuffer, sizeof(queueContext->InFlightRxBuffer));
                queueContext->RxCredit = 0;
            }
            if (flags & PURGE_TXCLEAR) {
                RingBufferInitialize(&queueContext->BusinessRequests, queueContext->BusinessBuffer, sizeof(queueContext->BusinessBuffer));
                RingBufferInitialize(&queueContext->InFlightTx, queueContext->InFlightTxBuffer, sizeof(queueContext->InFlightTxBuffer));
                queueContext->TxCredit = 0;
            }
            if (flags & PURGE_RXABORT) WdfIoQueuePurge(queueContext->ReadQueue, NULL, NULL);
        }
        break;
    }

    case IOCTL_SIM_VERSION:
    {
        ULONG version = SIM_CONTROL_PROTOCOL_VERSION;
        status = RequestCopyFromBuffer(Request, &version, sizeof(version));
        WdfRequestCompleteWithInformation(Request, status, NT_SUCCESS(status) ? sizeof(version) : 0);
        return;
    }
    case IOCTL_SIM_GET_SERIAL_CONFIG:
    {
        SIM_SERIAL_CONFIG config = {0};
        config.BaudRate = deviceContext->BaudRate;
        config.DataBits = deviceContext->SerialDataBits;
        config.Parity = deviceContext->SerialParity;
        config.StopBits = deviceContext->SerialStopBits;
        config.FlowControl = deviceContext->SerialFlowControl;
        status = RequestCopyFromBuffer(Request, &config, sizeof(config));
        WdfRequestCompleteWithInformation(Request, status, NT_SUCCESS(status) ? sizeof(config) : 0);
        return;
    }
    case IOCTL_SIM_SET_SERIAL_CONFIG:
    {
        SIM_SERIAL_CONFIG config = {0};
        status = RequestCopyToBuffer(Request, &config, sizeof(config));
        if (NT_SUCCESS(status) && InputBufferLength != sizeof(config))
        {
            status = STATUS_INVALID_PARAMETER;
        }
        if (NT_SUCCESS(status))
        {
            status = SetSerialConfig(
                deviceContext,
                config.BaudRate,
                config.DataBits,
                config.Parity,
                config.StopBits,
                config.FlowControl);
            if (NT_SUCCESS(status)) {
                queueContext->RxCredit = 0;
                queueContext->TxCredit = 0;
                queueContext->LastLineTick = GetTickCount64();
                if ((config.FlowControl & 1) == 0) QueuePumpLine(queueContext);
            }
        }
        WdfRequestCompleteWithInformation(Request, status, NT_SUCCESS(status) ? sizeof(config) : 0);
        return;
    }
    case IOCTL_SIM_RECEIVE:
    {
        PVOID buffer;
        size_t copied = 0;
        status = WdfRequestRetrieveOutputBuffer(Request, 1, &buffer, NULL);
        if (NT_SUCCESS(status)) {
            status = RingBufferRead(&queueContext->BusinessRequests, buffer, OutputBufferLength, &copied);
        }
        WdfRequestCompleteWithInformation(Request, status, NT_SUCCESS(status) ? copied : 0);
        return;
    }
    case IOCTL_SIM_INJECT:
    {
        PVOID buffer;
        size_t space;
        status = WdfRequestRetrieveInputBuffer(Request, 1, &buffer, NULL);
        if (NT_SUCCESS(status)) {
            size_t queued;
            RingBufferGetAvailableData(&queueContext->InFlightRx, &queued);
            PRING_BUFFER target = ((deviceContext->SerialFlowControl & 1) != 0 || queued != 0) ?
                &queueContext->InFlightRx : &queueContext->RingBuffer;
            RingBufferGetAvailableSpace(target, &space);
            status = InputBufferLength > space ? STATUS_BUFFER_OVERFLOW :
                RingBufferWrite(target, buffer, InputBufferLength);
        }
        WdfRequestCompleteWithInformation(Request, status, NT_SUCCESS(status) ? InputBufferLength : 0);
        if (NT_SUCCESS(status) && (deviceContext->SerialFlowControl & 1) == 0) {
            QueuePumpLine(queueContext);
            QueueWakeReaders(queueContext);
        }
        return;
    }

    case IOCTL_SERIAL_SET_BAUD_RATE:
    {
        //
        // This is a driver for a virtual serial port. Since there is no
        // actual hardware, we just store the baud rate and don't do
        // anything with it.
        //
        SERIAL_BAUD_RATE baudRateBuffer = {0};

        status = RequestCopyToBuffer(Request,
                            &baudRateBuffer,
                            sizeof(baudRateBuffer));
        //
        // The simulator owns the public serial configuration. Accept the
        // request for compatibility, but do not let the business client
        // overwrite the simulator-selected baud rate.
        //
        break;
    }

    case IOCTL_SERIAL_GET_BAUD_RATE:
    {
        SERIAL_BAUD_RATE baudRateBuffer = {0};

        baudRateBuffer.BaudRate = GetBaudRate(deviceContext);

        status = RequestCopyFromBuffer(Request,
                            &baudRateBuffer,
                            sizeof(baudRateBuffer));
        break;
    }

    case IOCTL_SERIAL_SET_MODEM_CONTROL:
    {
        //
        // Store the modem control register and mirror DTR/RTS so the
        // simulator can observe the business-side output lines.
        //
        ULONG *modemControlRegister = GetModemControlRegisterPtr(deviceContext);

        ASSERT(modemControlRegister);

        status = RequestCopyToBuffer(Request,
                            modemControlRegister,
                            sizeof(ULONG));
        if (NT_SUCCESS(status)) {
            deviceContext->DtrOn =
                (*modemControlRegister & SIM_SERIAL_DTR_CONTROL) != 0;
            deviceContext->RtsOn =
                (*modemControlRegister & SIM_SERIAL_RTS_CONTROL) != 0;
        }
        break;
    }

    case IOCTL_SERIAL_GET_MODEM_CONTROL:
    {
        ULONG *modemControlRegister = GetModemControlRegisterPtr(deviceContext);

        ASSERT(modemControlRegister);

        status = RequestCopyFromBuffer(Request,
                            modemControlRegister,
                            sizeof(ULONG));
        break;
    }

    case IOCTL_SERIAL_SET_FIFO_CONTROL:
    {
        //
        // This is a driver for a virtual serial port. Since there is no
        // actual hardware, we just store the FIFO control register
        // configuration and don't do anything with it.
        //
        ULONG *fifoControlRegister = GetFifoControlRegisterPtr(deviceContext);

        ASSERT(fifoControlRegister);

        status = RequestCopyToBuffer(Request,
                            fifoControlRegister,
                            sizeof(ULONG));
        break;
    }

    case IOCTL_SERIAL_GET_LINE_CONTROL:
    {
        status = QueueProcessGetLineControl(
                            queueContext,
                            Request);
        break;
    }


    case IOCTL_SERIAL_SET_LINE_CONTROL:
    {
        SERIAL_LINE_CONTROL ignored = {0};
        status = RequestCopyToBuffer(Request, &ignored, sizeof(ignored));
        //
        // The control interface is the sole owner of data bits, parity and
        // stop bits. Standard clients still receive success for compatibility.
        //
        break;
    }

    case IOCTL_SERIAL_GET_TIMEOUTS:
    {
        SERIAL_TIMEOUTS timeoutValues = {0};

        status = RequestCopyFromBuffer(Request,
                            (void*) &timeoutValues,
                            sizeof(timeoutValues));
        break;
    }

    case IOCTL_SERIAL_SET_TIMEOUTS:
    {
        SERIAL_TIMEOUTS timeoutValues = {0};

        status = RequestCopyToBuffer(Request,
                            (void*) &timeoutValues,
                            sizeof(timeoutValues));

        if( NT_SUCCESS(status) )
        {
            if ((timeoutValues.ReadIntervalTimeout        == MAXULONG) &&
                (timeoutValues.ReadTotalTimeoutMultiplier == MAXULONG) &&
                (timeoutValues.ReadTotalTimeoutConstant   == MAXULONG))
            {
                status = STATUS_INVALID_PARAMETER;
            }
        }

        if( NT_SUCCESS(status) ) {
            SetTimeouts(deviceContext, timeoutValues);
        }

        break;
    }

    case IOCTL_SERIAL_WAIT_ON_MASK:
    {
        //
        // NOTE: A wait-on-mask request should not be completed until either:
        //  1) A wait event occurs; or
        //  2) A set-wait-mask request is received
        //
        // This is a driver for a virtual serial port. Since there is no
        // actual hardware, we complete the request with some failure code.
        //
        WDFREQUEST savedRequest;
        ULONG eventMask = EV_RXCHAR;
        size_t available = 0;

        RingBufferGetAvailableData(&queueContext->RingBuffer, &available);
        if (available > 0 && (queueContext->WaitMask & EV_RXCHAR) != 0) {
            status = RequestCopyFromBuffer(Request, &eventMask, sizeof(eventMask));
            WdfRequestCompleteWithInformation(Request, status,
                NT_SUCCESS(status) ? sizeof(eventMask) : 0);
            return;
        }

        status = WdfIoQueueRetrieveNextRequest(
                            queueContext->WaitMaskQueue,
                            &savedRequest);

        if (NT_SUCCESS(status)) {
            WdfRequestComplete(savedRequest,
                            STATUS_UNSUCCESSFUL);
        }

        //
        // Keep the request in a manual queue and the framework will take
        // care of cancelling them when the app exits
        //
        status = WdfRequestForwardToIoQueue(
                            Request,
                            queueContext->WaitMaskQueue);

        if( !NT_SUCCESS(status) ) {
            Trace(TRACE_LEVEL_ERROR,
                "Error: WdfRequestForwardToIoQueue failed 0x%x", status);
            WdfRequestComplete(Request, status);
        }

        //
        // Instead of "break", use "return" to prevent the current request
        // from being completed.
        //
        return;
    }

    case IOCTL_SERIAL_SET_WAIT_MASK:
    {
        //
        // NOTE: If a wait-on-mask request is already pending when set-wait-mask
        // request is processed, the pending wait-on-event request is completed
        // with STATUS_SUCCESS and the output wait event mask is set to zero.
        //
        WDFREQUEST savedRequest;
        ULONG requestedMask = 0;

        status = RequestCopyToBuffer(Request, &requestedMask, sizeof(requestedMask));
        if (!NT_SUCCESS(status)) {
            break;
        }
        queueContext->WaitMask = requestedMask;

        status = WdfIoQueueRetrieveNextRequest(
                            queueContext->WaitMaskQueue,
                            &savedRequest);

        if (NT_SUCCESS(status)) {

            ULONG eventMask = 0;
            status = RequestCopyFromBuffer(
                            savedRequest,
                            &eventMask,
                            sizeof(eventMask));

            WdfRequestComplete(savedRequest, status);
        }

        //
        // NOTE: The application expects STATUS_SUCCESS for these IOCTLs.
        //
        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_SERIAL_GET_WAIT_MASK:
    {
        ULONG eventMask = queueContext->WaitMask;
        status = RequestCopyFromBuffer(Request, &eventMask, sizeof(eventMask));
        break;
    }

    case IOCTL_SERIAL_SET_HANDFLOW:
    {
        SIM_HANDFLOW flow = {0};
        status = RequestCopyToBuffer(Request, &flow, sizeof(flow));
        if (NT_SUCCESS(status)) {
            queueContext->HandflowControl = flow.ControlHandShake;
            queueContext->HandflowReplace = flow.FlowReplace;
            queueContext->XonLimit = flow.XonLimit;
            queueContext->XoffLimit = flow.XoffLimit;
            QueuePumpLine(queueContext);
        }
        break;
    }

    case IOCTL_SERIAL_SET_DTR:
    {
        ULONG *modemControlRegister = GetModemControlRegisterPtr(deviceContext);
        deviceContext->DtrOn = TRUE;
        *modemControlRegister |= SIM_SERIAL_DTR_CONTROL;
        status = STATUS_SUCCESS;
        break;
    }
    case IOCTL_SERIAL_CLR_DTR:
    {
        ULONG *modemControlRegister = GetModemControlRegisterPtr(deviceContext);
        deviceContext->DtrOn = FALSE;
        *modemControlRegister &= ~SIM_SERIAL_DTR_CONTROL;
        status = STATUS_SUCCESS;
        break;
    }
    case IOCTL_SERIAL_SET_RTS:
    {
        ULONG *modemControlRegister = GetModemControlRegisterPtr(deviceContext);
        deviceContext->RtsOn = TRUE;
        *modemControlRegister |= SIM_SERIAL_RTS_CONTROL;
        status = STATUS_SUCCESS;
        break;
    }
    case IOCTL_SERIAL_CLR_RTS:
    {
        ULONG *modemControlRegister = GetModemControlRegisterPtr(deviceContext);
        deviceContext->RtsOn = FALSE;
        *modemControlRegister &= ~SIM_SERIAL_RTS_CONTROL;
        status = STATUS_SUCCESS;
        break;
    }
    case IOCTL_SERIAL_SET_QUEUE_SIZE:
    case IOCTL_SERIAL_SET_XON:
    case IOCTL_SERIAL_SET_XOFF:
    case IOCTL_SERIAL_SET_CHARS:
    case IOCTL_SERIAL_RESET_DEVICE:
        //
        // NOTE: The application expects STATUS_SUCCESS for these IOCTLs.
        //
        status = STATUS_SUCCESS;
        break;

    default:
        status = STATUS_INVALID_PARAMETER;
        break;
    }

    //
    // complete the request
    //
    WdfRequestComplete(Request, status);
}


VOID
EvtIoWrite(WDFQUEUE Queue, WDFREQUEST Request, size_t Length)
{
    PQUEUE_CONTEXT context = GetQueueContext(Queue);
    PVOID data;
    size_t space;
    NTSTATUS status;
    if (Length == 0) { WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, 0); return; }
    status = WdfRequestRetrieveInputBuffer(Request, Length, &data, NULL);
    if (NT_SUCCESS(status)) {
        size_t queued;
        RingBufferGetAvailableData(&context->InFlightTx, &queued);
        BOOLEAN ctsHeld = (context->HandflowControl & SIM_CTS_HANDSHAKE) != 0 &&
            (context->ModemStatus & MS_CTS_ON) == 0;
        PRING_BUFFER target = ((context->DeviceContext->SerialFlowControl & 1) != 0 || queued != 0 || ctsHeld) ?
            &context->InFlightTx : &context->BusinessRequests;
        RingBufferGetAvailableSpace(target, &space);
        status = Length > space ? STATUS_BUFFER_OVERFLOW :
            RingBufferWrite(target, data, Length);
    }
    if (NT_SUCCESS(status) && (context->DeviceContext->SerialFlowControl & 1) == 0)
        QueuePumpLine(context);
    WdfRequestCompleteWithInformation(Request, status, NT_SUCCESS(status) ? Length : 0);
}
VOID
EvtIoRead(
    _In_  WDFQUEUE          Queue,
    _In_  WDFREQUEST        Request,
    _In_  size_t            Length
    )
{
    NTSTATUS                status;
    PQUEUE_CONTEXT          queueContext = GetQueueContext(Queue);
    WDFMEMORY               memory;
    size_t                  bytesCopied = 0;

    Trace(TRACE_LEVEL_INFO,
            "EvtIoRead 0x%p", Request);

    status = WdfRequestRetrieveOutputMemory(Request, &memory);
    if( !NT_SUCCESS(status) ) {
        Trace(TRACE_LEVEL_ERROR,
            "Error: WdfRequestRetrieveOutputMemory failed 0x%x", status);
        WdfRequestComplete(Request, status);
        return;
    }

    status = RingBufferRead(&queueContext->RingBuffer,
                            (BYTE*)WdfMemoryGetBuffer(memory, NULL),
                            Length,
                            &bytesCopied);
    if( !NT_SUCCESS(status) ) {
        WdfRequestComplete(Request, status);
        return;
    }

    if (bytesCopied > 0) {
        //
        // Data was read from buffer succesfully
        //
        WdfRequestCompleteWithInformation(Request, status, bytesCopied);
        return;
    }
    else {
        //
        // No data to read. UMDF rejects forwarding this request to a manual
        // queue (STATUS_DEVICE_BUSY -> ERROR_BUSY on the client), so the
        // driver keeps the request itself and the line pump completes it
        // once data arrives.
        //
        WdfWaitLockAcquire(queueContext->PendingReadsLock, NULL);
        if (queueContext->PendingReadCount >= MAX_PENDING_READS) {
            WdfWaitLockRelease(queueContext->PendingReadsLock);
            WdfRequestComplete(Request, STATUS_DEVICE_BUSY);
            return;
        }

        queueContext->PendingReads[queueContext->PendingReadCount++] = Request;
        status = WdfRequestMarkCancelableEx(Request, QueueEvtReadCancel);
        if (!NT_SUCCESS(status)) {
            QueueRemovePendingReadLocked(queueContext, Request);
        }
        WdfWaitLockRelease(queueContext->PendingReadsLock);

        if (!NT_SUCCESS(status)) {
            WdfRequestComplete(Request, status);
        }
    }
}


NTSTATUS
QueueProcessWriteBytes(
    _In_  PQUEUE_CONTEXT    QueueContext,
    _In_reads_bytes_(Length)
          PUCHAR            Characters,
    _In_  size_t            Length
    )
/*++
Routine Description:

    This function is called when the framework receives IRP_MJ_WRITE
    requests from the system. The write event handler(FmEvtIoWrite) calls ProcessWriteBytes.
    It parses the Characters passed in and looks for the  for sequences "AT" -ok  ,
    "ATA" --CONNECT, ATD<number> -- CONNECT and sets the state of the device appropriately.
    These bytes are placed in the read Buffer to be processed later since this device
    works in a loopback fashion.

Arguments:

    Characters - Pointer to the write IRP's system buffer.

    Length - Length of the IO operation
                 The default property of the queue is to not dispatch
                 zero lenght read & write requests to the driver and
                 complete is with status success. So we will never get
                 a zero length request.
--*/
{
    NTSTATUS                status = STATUS_SUCCESS;
    UCHAR                   currentCharacter;
    UCHAR                   connectString[]  = "\r\nCONNECT\r\n";
    UCHAR                   connectStringCch = ARRAY_SIZE(connectString) - 1;
    UCHAR                   okString[]       = "\r\nOK\r\n";
    UCHAR                   okStringCch      = ARRAY_SIZE(okString) - 1;

    while (Length != 0) {

        currentCharacter = *(Characters++);
        Length--;

        if(currentCharacter == '\0') {
            continue;
        }

        status = RingBufferWrite(&QueueContext->RingBuffer,
                            &currentCharacter,
                            sizeof(currentCharacter));
        if( !NT_SUCCESS(status) ) {
            return status;
        }

        switch (QueueContext->CommandMatchState) {

        case COMMAND_MATCH_STATE_IDLE:

            if ((currentCharacter == 'a') || (currentCharacter == 'A')) {
                //
                //  got an A
                //
                QueueContext->CommandMatchState = COMMAND_MATCH_STATE_GOT_A;
                QueueContext->ConnectCommand = FALSE;
                QueueContext->IgnoreNextChar = FALSE;
            }
            break;

        case COMMAND_MATCH_STATE_GOT_A:

            if ((currentCharacter == 't') || (currentCharacter == 'T')) {
                //
                //  got a T
                //
                QueueContext->CommandMatchState = COMMAND_MATCH_STATE_GOT_T;
            }
            else {
                QueueContext->CommandMatchState = COMMAND_MATCH_STATE_IDLE;
            }

            break;

        case COMMAND_MATCH_STATE_GOT_T:

            if (! QueueContext->IgnoreNextChar) {
                //
                //  the last char was not a special char
                //  check for CONNECT command
                //
                if ((currentCharacter == 'A') || (currentCharacter == 'a')) {
                    QueueContext->ConnectCommand = TRUE;
                }

                if ((currentCharacter == 'D') || (currentCharacter == 'd')) {
                    QueueContext->ConnectCommand = TRUE;
                }
            }

            QueueContext->IgnoreNextChar = TRUE;

            if (currentCharacter == '\r') {
                //
                //  got a CR, send a response to the command
                //
                QueueContext->CommandMatchState = COMMAND_MATCH_STATE_IDLE;

                if (QueueContext->ConnectCommand) {
                    //
                    //  place <cr><lf>CONNECT<cr><lf>  in the buffer
                    //
                    status = RingBufferWrite(&QueueContext->RingBuffer,
                            connectString,
                            connectStringCch);
                    if( !NT_SUCCESS(status) ) {
                        return status;
                    }
                    //
                    //  connected now raise CD
                    //
                    QueueContext->CurrentlyConnected = TRUE;
                    QueueContext->ConnectionStateChanged = TRUE;
                }
                else {
                    //
                    //  place <cr><lf>OK<cr><lf>  in the buffer
                    //
                    status = RingBufferWrite(&QueueContext->RingBuffer,
                            okString,
                            okStringCch);
                    if( !NT_SUCCESS(status) ) {
                        return status;
                    }
                }
            }
            break;

        default:
            break;
        }
    }
    return status;
}


NTSTATUS
QueueProcessGetLineControl(
    _In_  PQUEUE_CONTEXT    QueueContext,
    _In_  WDFREQUEST        Request
    )
{
    NTSTATUS                status;
    PDEVICE_CONTEXT         deviceContext;
    SERIAL_LINE_CONTROL     lineControl = {0};
    ULONG                   lineControlSnapshot;
    ULONG                   *lineControlRegister;

    deviceContext = QueueContext->DeviceContext;
    lineControlRegister = GetLineControlRegisterPtr(deviceContext);

    ASSERT(lineControlRegister);

    //
    // Take a snapshot of the line control register variable
    //
    lineControlSnapshot = ReadNoFence((LONG *)lineControlRegister);

    //
    // Decode the word length
    //
    if ((lineControlSnapshot & SERIAL_DATA_MASK) == SERIAL_5_DATA)
    {
        lineControl.WordLength = 5;
    }
    else if ((lineControlSnapshot & SERIAL_DATA_MASK) == SERIAL_6_DATA)
    {
        lineControl.WordLength = 6;
    }
    else if ((lineControlSnapshot & SERIAL_DATA_MASK) == SERIAL_7_DATA)
    {
        lineControl.WordLength = 7;
    }
    else if ((lineControlSnapshot & SERIAL_DATA_MASK) == SERIAL_8_DATA)
    {
        lineControl.WordLength = 8;
    }

    //
    // Decode the parity
    //
    if ((lineControlSnapshot & SERIAL_PARITY_MASK) == SERIAL_NONE_PARITY)
    {
        lineControl.Parity = NO_PARITY;
    }
    else if ((lineControlSnapshot & SERIAL_PARITY_MASK) == SERIAL_ODD_PARITY)
    {
        lineControl.Parity = ODD_PARITY;
    }
    else if ((lineControlSnapshot & SERIAL_PARITY_MASK) == SERIAL_EVEN_PARITY)
    {
        lineControl.Parity = EVEN_PARITY;
    }
    else if ((lineControlSnapshot & SERIAL_PARITY_MASK) == SERIAL_MARK_PARITY)
    {
        lineControl.Parity = MARK_PARITY;
    }
    else if ((lineControlSnapshot & SERIAL_PARITY_MASK) == SERIAL_SPACE_PARITY)
    {
        lineControl.Parity = SPACE_PARITY;
    }

    //
    // Decode the length of the stop bit
    //
    if (lineControlSnapshot & SERIAL_2_STOP)
    {
        if (lineControl.WordLength == 5)
        {
            lineControl.StopBits = STOP_BITS_1_5;
        }
        else
        {
            lineControl.StopBits = STOP_BITS_2;
        }
    }
    else
    {
        lineControl.StopBits = STOP_BIT_1;
    }

    //
    // Copy the information that was decoded to the caller's buffer
    //
    status = RequestCopyFromBuffer(Request,
                        (void*) &lineControl,
                        sizeof(lineControl));
    return status;
}


NTSTATUS
QueueProcessSetLineControl(
    _In_  PQUEUE_CONTEXT    QueueContext,
    _In_  WDFREQUEST        Request
    )
{
    NTSTATUS                status;
    PDEVICE_CONTEXT         deviceContext;
    SERIAL_LINE_CONTROL     lineControl = {0};
    ULONG                   *lineControlRegister;
    UCHAR                   lineControlData = 0;
    UCHAR                   lineControlStop = 0;
    UCHAR                   lineControlParity = 0;
    ULONG                   lineControlSnapshot;
    ULONG                   lineControlNew;
    ULONG                   lineControlPrevious;
    ULONG                   i;

    deviceContext = QueueContext->DeviceContext;
    lineControlRegister = GetLineControlRegisterPtr(deviceContext);

    ASSERT(lineControlRegister);

    //
    // This is a driver for a virtual serial port. Since there is no
    // actual hardware, we just store the line control register
    // configuration and don't do anything with it.
    //
    status = RequestCopyToBuffer(Request,
                        (void*) &lineControl,
                        sizeof(lineControl));

    //
    // Bits 0 and 1 of the line control register
    //
    if( NT_SUCCESS(status) )
    {
        switch (lineControl.WordLength)
        {
        case 5:
            lineControlData = SERIAL_5_DATA;
            SetValidDataMask(deviceContext, 0x1f);
            break;

        case 6:
            lineControlData = SERIAL_6_DATA;
            SetValidDataMask(deviceContext, 0x3f);
            break;

        case 7:
            lineControlData = SERIAL_7_DATA;
            SetValidDataMask(deviceContext, 0x7f);
            break;

        case 8:
            lineControlData = SERIAL_8_DATA;
            SetValidDataMask(deviceContext, 0xff);
            break;

        default:
            status = STATUS_INVALID_PARAMETER;
            break;
        }
    }

    //
    // Bit 2 of the line control register
    //
    if( NT_SUCCESS(status) )
    {
        switch (lineControl.StopBits)
        {
        case STOP_BIT_1:
            lineControlStop = SERIAL_1_STOP;
            break;

        case STOP_BITS_1_5:
            if (lineControlData != SERIAL_5_DATA)
            {
                status = STATUS_INVALID_PARAMETER;
                break;
            }
            lineControlStop = SERIAL_1_5_STOP;
            break;

        case STOP_BITS_2:
            if (lineControlData == SERIAL_5_DATA)
            {
                status = STATUS_INVALID_PARAMETER;
                break;
            }
            lineControlStop = SERIAL_2_STOP;
            break;

        default:
            status = STATUS_INVALID_PARAMETER;
            break;
        }
    }

    //
    // Bits 3, 4 and 5 of the line control register
    //
    if( NT_SUCCESS(status) )
    {
        switch (lineControl.Parity)
        {
        case NO_PARITY:
            lineControlParity = SERIAL_NONE_PARITY;
            break;

        case EVEN_PARITY:
            lineControlParity = SERIAL_EVEN_PARITY;
            break;

        case ODD_PARITY:
            lineControlParity = SERIAL_ODD_PARITY;
            break;

        case SPACE_PARITY:
            lineControlParity = SERIAL_SPACE_PARITY;
            break;

        case MARK_PARITY:
            lineControlParity = SERIAL_MARK_PARITY;
            break;

        default:
            status = STATUS_INVALID_PARAMETER;
            break;
        }
    }

    //
    // Update our line control register variable atomically
    //
    i=0;
    do {
        i++;
        if ((i & 0xf) == 0) {
            //
            // We've been spinning in a loop for a while trying to
            // update the line control register variable atomically.
            // Yield the CPU for other threads for a while.
            //
#ifdef _KERNEL_MODE
            LARGE_INTEGER   interval;
            interval.QuadPart = 0;
            KeDelayExecutionThread(UserMode, FALSE, &interval);
#else
            SwitchToThread();
#endif
        }

        lineControlSnapshot = ReadNoFence((LONG *)lineControlRegister);

        lineControlNew = (lineControlSnapshot & SERIAL_LCR_BREAK) |
                        (lineControlData | lineControlParity | lineControlStop);

        lineControlPrevious = InterlockedCompareExchange(
                      (LONG *) lineControlRegister,
                       lineControlNew,
                       lineControlSnapshot);

    } while (lineControlPrevious != lineControlSnapshot);

    return status;
}
