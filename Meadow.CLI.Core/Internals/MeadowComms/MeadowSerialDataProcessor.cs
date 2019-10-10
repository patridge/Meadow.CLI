﻿using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Meadow.CLI.Internals.MeadowComms.RecvClasses;
using MeadowCLI.DeviceManagement;

namespace MeadowCLI.Hcom
{
    public enum MeadowMessageType
    {
        AppOutput,
        DeviceInfo,
        FileListTitle,
        FileListMember,
        Data,
    }

    public enum HcomProtocolCtrl
    {
        // Must match set in MeadowOS
        HcomProtoCtrlRequestUndefined = 0,
        HcomProtoCtrlRequestRejected = 1,
        HcomProtoCtrlRequestAccepted = 2,
        HcomProtoCtrlRequestEnded = 3,
        HcomProtoCtrlRequestError = 4,
        HcomProtoCtrlRequestInformation = 5,
        HcomProtoCtrlRequestFileListHeader = 6,
        HcomProtoCtrlRequestFileListMember = 7,
        HcomProtoCtrlRequestMonoMessage = 8,
        HcomProtoCtrlRequestDeviceInfo = 9,
        HcomProtoCtrlRequestDeviceDiag = 10,
    }

    public class MeadowMessageEventArgs : EventArgs
    {
        public string Message { get; private set; }
        public MeadowMessageType MessageType { get; private set; }

        public MeadowMessageEventArgs (MeadowMessageType messageType, string message)
        {
            Message = message;
            MessageType = messageType;
        }
    }

    public class MeadowSerialDataProcessor
    {   //collapse to one and use enum
        public EventHandler<MeadowMessageEventArgs> OnReceiveData;
        HostCommBuffer _hostCommBuffer;
        RecvFactoryManager _recvFactoryManager;
        readonly SerialPort serialPort;

        const int MAX_RECEIVED_BYTES = 2048;

        // It seems that the .Net SerialPort class is not all it could be.
        // To acheive reliable operation some SerialPort class methods must
        // not be used. When receiving, the BaseStream must be used.
        // http://www.sparxeng.com/blog/software/must-use-net-system-io-ports-serialport

        //-------------------------------------------------------------
        // Constructor
        public MeadowSerialDataProcessor(SerialPort serialPort)
        {
            this.serialPort = serialPort;
            _recvFactoryManager = new RecvFactoryManager();
            _hostCommBuffer = new HostCommBuffer();
            _hostCommBuffer.Init(MeadowDeviceManager.maxAllowableDataBlock * 4);    // room for 4 messages

            ReadPortAsync(); 
        }

        //-------------------------------------------------------------
        // All received data handled here
        private async Task ReadPortAsync()
        {
            int offset = 0;
            byte[] buffer = new byte[MAX_RECEIVED_BYTES];

            try
            {
                while (true)
                {
                    var byteCount = Math.Min(serialPort.BytesToRead, buffer.Length);

                    if (byteCount > 0)
                    {
                        var receivedLength = await serialPort.BaseStream.ReadAsync(buffer, offset, byteCount).ConfigureAwait(false);
                        offset = AddAndProcessData(buffer, receivedLength + offset);
                        Debug.Assert(offset > -1);
                    }
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }
            catch (ThreadAbortException ex)
            {
                //ignoring for now until we wire cancelation ...
                //this blocks the thread abort exception when the console app closes
            }
            catch (InvalidOperationException)
            {
                // common if the port is reset (e.g. mono enable/disable) - don't spew confusing info
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception: {ex} may mean the target connection dropped");
            }
        }

        int AddAndProcessData(byte[] buffer, int availableBytes)
        {
            HcomBufferReturn result;

            while (true)
            {
                // add these bytes to the circular buffer
                result = _hostCommBuffer.AddBytes(buffer, 0, availableBytes);
                if(result == HcomBufferReturn.HCOM_CIR_BUF_ADD_SUCCESS)
                {
                    break;
                }
                else if(result == HcomBufferReturn.HCOM_CIR_BUF_ADD_WONT_FIT)
                {
                    // Wasn't possible to put these bytes in the buffer. We need to
                    // process a few packets and then retry to add this data
                    result = PullAndProcessAllPackets();
                    if (result == HcomBufferReturn.HCOM_CIR_BUF_GET_FOUND_MSG)
                        continue;   // There should be room now for the failed add

                    if(result == HcomBufferReturn.HCOM_CIR_BUF_GET_NONE_FOUND ||
                        result == HcomBufferReturn.HCOM_CIR_BUF_GET_BUF_NO_ROOM)
                    {
                        // This should never happen
                        Debug.Assert(false);
                    }
                }
                else if(result == HcomBufferReturn.HCOM_CIR_BUF_ADD_BAD_ARG)
                {
                    // Something wrong with implemenation
                    Debug.Assert(false);
                }
                else
                {
                    // Undefined return value????
                    Debug.Assert(false);
                }
            }

            result = PullAndProcessAllPackets();
            Debug.Assert(result == HcomBufferReturn.HCOM_CIR_BUF_GET_FOUND_MSG ||
                result == HcomBufferReturn.HCOM_CIR_BUF_GET_NONE_FOUND);

            return 0;
        }

        HcomBufferReturn PullAndProcessAllPackets()
        {
            byte[] packetBuffer = new byte[MeadowDeviceManager.maxSizeOfXmitPacket];
            byte[] decodedBuffer = new byte[MeadowDeviceManager.maxAllowableDataBlock];
            int packetLength;
            HcomBufferReturn result;

            while (true)
            {
                result = _hostCommBuffer.GetNextPacket(packetBuffer, MeadowDeviceManager.maxAllowableDataBlock, out packetLength);
                if (result == HcomBufferReturn.HCOM_CIR_BUF_GET_NONE_FOUND)
                    return result;

                if(result == HcomBufferReturn.HCOM_CIR_BUF_GET_BUF_NO_ROOM)
                {
                    // Implementation problem the packetBuffer is too small
                    Debug.Assert(false);
                }

                // Only other possible outcome is success
                Debug.Assert(result == HcomBufferReturn.HCOM_CIR_BUF_GET_FOUND_MSG);

                // It's possible that we may find a series of 0x00 values in the buffer.
                // This is because when the sender is blocked (because this code isn't
                // running) it will attempt to send single 0x00 before the full message.
                // This allows it to test for a connection. So when the connection is
                // unblocked this 0x00 is sent and gets put into the buffer.
                if (packetLength == 1)
                    continue;

                int decodedSize = CobsTools.CobsDecoding(packetBuffer, --packetLength, ref decodedBuffer);
                Debug.Assert(decodedSize <= MeadowDeviceManager.maxAllowableDataBlock);

                // Process the received packet
                if(decodedSize > 0)
                {
                    bool procResult = ParseAndProcessDecodedPacket(decodedBuffer, decodedSize);
                    if (procResult)
                        continue;   // See if there's another packet ready
                }

                // All errors just leave
                break;
            }
            return result;
        }

        bool ParseAndProcessDecodedPacket(byte[] receivedMsg, int receivedMsgLen)
        {
            try
            {
                IReceivedMessage processor = _recvFactoryManager.CreateProcessor(receivedMsg);
                if (processor.Execute(receivedMsg, receivedMsgLen))
                {
                    ProcessRecvdMessage(processor.ToString(), processor.ProtocolCtrl);
                    return true;
                }
                else
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception: {ex}");
                return false;
            }
        }

        void ProcessRecvdMessage(string meadowMessage, ushort protocolCtrl)
        {
            switch ((HcomProtocolCtrl)protocolCtrl)
            {
                case HcomProtocolCtrl.HcomProtoCtrlRequestUndefined:
                    Console.WriteLine("new-Request Undefined received"); // TESTING
                    break;
                case HcomProtocolCtrl.HcomProtoCtrlRequestRejected:
                    Console.WriteLine("new-Request Rejected received"); // TESTING
                    if (!String.IsNullOrEmpty(meadowMessage))
                        OnReceiveData?.Invoke(this, new MeadowMessageEventArgs(MeadowMessageType.Data, meadowMessage));
                    break;
                case HcomProtocolCtrl.HcomProtoCtrlRequestAccepted:
                    Console.WriteLine("new-Request Accepted received"); // TESTING
                    break;
                case HcomProtocolCtrl.HcomProtoCtrlRequestEnded:
                    Console.WriteLine("new-Request Ended received"); // TESTING
                    break;
                case HcomProtocolCtrl.HcomProtoCtrlRequestError:
                    Console.WriteLine("new-Request Error received"); // TESTING
                    if (!String.IsNullOrEmpty(meadowMessage))
                        OnReceiveData?.Invoke(this, new MeadowMessageEventArgs(MeadowMessageType.Data, meadowMessage));
                    break;
                case HcomProtocolCtrl.HcomProtoCtrlRequestInformation:
                    Console.WriteLine("new-Request Information received"); // TESTING
                    if (!String.IsNullOrEmpty(meadowMessage))
                        OnReceiveData?.Invoke(this, new MeadowMessageEventArgs(MeadowMessageType.Data, meadowMessage));
                    break;
                case HcomProtocolCtrl.HcomProtoCtrlRequestFileListHeader:
                    Console.WriteLine("new-Request File List Header received"); // TESTING
                    OnReceiveData?.Invoke(this, new MeadowMessageEventArgs(MeadowMessageType.FileListTitle, meadowMessage));
                  break;
                case HcomProtocolCtrl.HcomProtoCtrlRequestFileListMember:
                    OnReceiveData?.Invoke(this, new MeadowMessageEventArgs(MeadowMessageType.FileListMember, meadowMessage));
                    break;
                case HcomProtocolCtrl.HcomProtoCtrlRequestMonoMessage:
                    OnReceiveData?.Invoke(this, new MeadowMessageEventArgs(MeadowMessageType.AppOutput, meadowMessage));
                    break;
                case HcomProtocolCtrl.HcomProtoCtrlRequestDeviceInfo:
                    OnReceiveData?.Invoke(this, new MeadowMessageEventArgs(MeadowMessageType.DeviceInfo, meadowMessage));
                    break;
                case HcomProtocolCtrl.HcomProtoCtrlRequestDeviceDiag:
                    // Console.WriteLine("new-Request Device Diag received"); // TESTING
                    if (!String.IsNullOrEmpty(meadowMessage))
                        OnReceiveData?.Invoke(this, new MeadowMessageEventArgs(MeadowMessageType.Data, meadowMessage));
                    break;
                default:
                    Console.WriteLine("Received: default ");
                    throw new ArgumentException("Unknown protocol control type");
            }
        }

    }
}