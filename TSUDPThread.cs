﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;

namespace opentuner
{
    public class TSUDPThread
    {
        ConcurrentQueue<byte> _ts_data_queue = new ConcurrentQueue<byte>();

        object locker = new object();

        protected bool _stream;
        public bool stream
        {
            get
            {
                lock (locker)
                    return _stream;
            }
            set
            {
                lock (locker)
                    _stream = value;
            }
        }

        bool streaming = false;


        public TSUDPThread(TSThread _ts_thread)
        {
            _ts_thread.RegisterTSConsumer(_ts_data_queue);
        }

        public void worker_thread()
        {
            byte data;

            // Create a UDP client to send data to VLC
            UdpClient udpClient = new UdpClient();

            // Set the destination IP address and port of VLC
            IPAddress vlcIpAddress = IPAddress.Parse("127.0.0.1"); // replace with the actual IP address of VLC
            int vlcPort = 9080; // replace with the actual port number used by VLC

            try
            {
                while (true)
                {

                    if (streaming == false && stream == true)
                    {
                        streaming = true;
                    }
                    else
                    {
                        if (streaming == true && stream == false)
                        {
                            streaming = false;
                        }
                    }

                    int ts_data_count = _ts_data_queue.Count();

                    if (ts_data_count >= 188)
                    {
                        byte[] dt = new byte[188];
                        int count = 0;

                        while (count < 188)
                        {
                            if (_ts_data_queue.TryDequeue(out data))
                                dt[count++] = data;
                        }

                        if (streaming)
                        {
                            udpClient.Send(dt, count, new IPEndPoint(vlcIpAddress, vlcPort));
                        }
                    }
                    else
                    {
                        Thread.Sleep(100);
                    }
                }
            }
            catch (ThreadAbortException)
            {
                Console.WriteLine("TS UDP Thread: Closing ");
            }
            finally
            {
                Console.WriteLine("Closing TS UDP");
            }
        }
    }
}