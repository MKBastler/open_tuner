﻿using Newtonsoft.Json;
using opentuner.MediaPlayers;
using opentuner.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.Remoting.Messaging;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Configuration;
using System.Windows.Forms;
using WebSocketSharp;

namespace opentuner.MediaSources.Longmynd
{
    public partial class LongmyndSource : OTSource
    {
        private bool _connected = false;

        private int _interface = 1; // 0 = ws, 1 = mqtt



        private System.Timers.Timer sessionTimer;

        private LongmyndSettings _settings = new LongmyndSettings();

        private int demodState = -1;

        private UDPClient udp_client;

        bool playing = false;

        private VideoChangeCallback VideoChangeCB;

        Thread ts_thread_t = null;
        TSThread ts_thread;

        // todo: fix double buffer read
        private CircularBuffer udp_buffer = new CircularBuffer(GlobalDefines.CircularBufferStartingCapacity);
        public CircularBuffer ts_data_queue = new CircularBuffer(GlobalDefines.CircularBufferStartingCapacity);

        private OTMediaPlayer _media_player;
        private TSRecorder _recorder;
        private TSUdpStreamer _streamer;

        public override bool DeviceConnected => _connected;

        // properties
        private uint current_frequency_1 = 0;

        public override void Close()
        {
            
        }

        public override void ConfigureVideoPlayers(List<OTMediaPlayer> MediaPlayers)
        {
            _media_player = MediaPlayers[0];
            _media_player.onVideoOut += _media_player_onVideoOut;
        }

        private void _media_player_onVideoOut(object sender, MediaStatus e)
        {
            _media_player.SetVolume(_settings.DefaultVolume1);
            UpdateMediaProperties(0, e);
        }

        public override string GetDescription()
        {
            return "Longmynd Client";
        }

        public override string GetDeviceName()
        {
            return "Longmynd Client Device";
        }

        public override long GetFrequency(int device, bool offset_included)
        {
            if (offset_included)
                return current_frequency_1 + _settings.Offset1;

            return current_frequency_1;
        }

        public override string GetName()
        {
            return "Longmynd";
        }

        public override CircularBuffer GetVideoDataQueue(int device)
        {
            return ts_data_queue;
        }

        public override int GetVideoSourceCount()
        {
            return 1;
        }

        private void Udp_client_DataReceived(object sender, byte[] e)
        {
            if (!playing) { return; }

            for (int c = 0; c < e.Length; c++)
            {
                udp_buffer.Enqueue(e[c]);
            }
        }

        private void Udp_client_ConnectionStatusChanged(object sender, string e)
        {
            debug("udp: Connection status changed: " + e);
        }



        public override int Initialize(VideoChangeCallback VideoChangeCB, Control Parent)
        {
            _parent = Parent;



            // connect websockets
            switch (_interface)
            {
                case 0:  
                    connectWebsockets(); 
                    break;
                case 1:
                    ConnectMqtt();
                    break;
            }

            // open udp port
            udp_client = new UDPClient(4003);
            udp_client.ConnectionStatusChanged += Udp_client_ConnectionStatusChanged;
            udp_client.DataReceived += Udp_client_DataReceived;
            udp_client.Connect();

            ts_thread = new TSThread(ts_data_queue, FlushTS2, ReadTS2, "LM TS");
            ts_thread_t = new Thread(ts_thread.worker_thread);
            ts_thread_t.Start();

            BuildSourceProperties();

            this.VideoChangeCB = VideoChangeCB;

            return 1;
        }

        void FlushTS2()
        {
            udp_buffer.Clear();
        }

        byte ReadTS2(ref byte[] data, ref uint dataRead)
        {
            int read = udp_buffer.Count;
            uint written = 0;

            if (udp_buffer.Count > 4000) 
            {
                read = 4000;
            }

            for (int c = 0; c < read; c++)
            {
                data[c] = udp_buffer.Dequeue();
                written += 1;
            }

            dataRead = written;

            return 0;
        }


        public override void RegisterTSConsumer(int device, CircularBuffer ts_buffer_queue)
        {
            ts_thread.RegisterTSConsumer(ts_buffer_queue);
        }


        public override void SetFrequency(int device, uint frequency, uint symbol_rate, bool offset_included)
        {
            switch (_interface)
            {
                case 0: 
                    WSSetFrequency(frequency, symbol_rate); break;
                case 1:
                    MqttSetFrequency(frequency, symbol_rate); break;
            }


            demodState = -1;
        }

        public override void ShowSettings()
        {
            
        }

        public override void StartStreaming(int device)
        {
            if (ts_thread != null)
                ts_thread.start_ts();
        }

        public override void StopStreaming(int device)
        {
            if (ts_thread != null)
                ts_thread.stop_ts();
        }

        public override void ConfigureTSRecorders(List<TSRecorder> TSRecorders)
        {
            _recorder = TSRecorders[0];
        }

        public override void ConfigureTSStreamers(List<TSUdpStreamer> TSStreamers)
        {
            _streamer = TSStreamers[0];
        }

        #region lookuptables
        // lookup tables - TODO: consolidate this with main lookups


        Dictionary<int, string> demod_state_lookup = new Dictionary<int, string>()
        {
            { 0 , "Initializing" },
            { 1 , "Hunting" },
            { 2 , "Header" },
            { 3 , "Lock DVB-S" },
            { 4 , "Lock DVB-S2" }
        };

        Dictionary<int, string> modcod_lookup_dvbs = new Dictionary<int, string>()
        {
            { 4 , "QPSK 1/2" },
            { 5 , "QPSK 3/5" },
            { 6 , "QPSK 2/3" },
            { 7 , "QPSK 3/4" },
            { 9 , "QPSK 5/6" },
            { 10 , "QPSK 6/7" },
            { 11 , "QPSK 7/8" }
        };

        // values obtained from longmynd.py in rydeplayer
        Dictionary<int, double> modcod_lookup_dvbs_threshold = new Dictionary<int, double>()
        {
            { 4 , 1.7 },
            { 5 , 4.8 }, // not sure about this one
            { 6 , 3.3 },
            { 7 , 4.2 },
            { 9 , 5.1 },
            { 10 , 5.5  },
            { 11 , 5.8 }
        };

        Dictionary<int, string> modcod_lookup_dvbs2 = new Dictionary<int, string>()
        {
            { 0, "DummyPL"},
            {  1, "QPSK 1/4"},
            {  2, "QPSK 1/3"},
            {  3, "QPSK 2/5"},
            {  4, "QPSK 1/2"},
            {  5, "QPSK 3/5"},
            {  6, "QPSK 2/3"},
            {  7, "QPSK 3/4"},
            {  8, "QPSK 4/5"},
            {  9, "QPSK 5/6"},
            {  10, "QPSK 8/9"},
            {  11, "QPSK 9/10"},
            {  12, "8PSK 3/5"},
            {  13, "8PSK 2/3"},
            {  14, "8PSK 3/4"},
            {  15, "8PSK 5/6"},
            {  16, "8PSK 8/9"},
            {  17, "8PSK 9/10"},
            {  18, "16APSK 2/3"},
            {  19, "16APSK 3/4"},
            {  20, "16APSK 4/5"},
            {  21, "16APSK 5/6"},
            {  22, "16APSK 8/9"},
            {  23, "16APSK 9/10"},
            {  24, "32APSK 3/4"},
            {  25, "32APSK 4/5"},
            {  26, "32APSK 5/6"},
            {  27, "32APSK 8/9"},
            {  28, "32APSK 9/10"}
        };

        Dictionary<int, double> modcod_lookup_dvbs2_threshold = new Dictionary<int, double>()
        {
            { 0, 0},
            {  1, -2.3},
            {  2, -1.2},
            {  3, -0.3},
            {  4, 1.0},
            {  5, 2.3},
            {  6, 3.1},
            {  7, 4.1},
            {  8, 4.7},
            {  9, 5.2},
            {  10, 6.2},
            {  11, 6.5},
            {  12, 5.5},
            {  13, 6.6},
            {  14, 7.9},
            {  15, 9.4},
            {  16, 10.7},
            {  17, 11.0},
            {  18, 9.0},
            {  19, 10.2},
            {  20, 11.0},
            {  21, 11.6},
            {  22, 12.9},
            {  23, 13.2},
            {  24, 12.8},
            {  25, 13.7},
            {  26, 14.3},
            {  27, 15.7},
            {  28, 16.1}
        };

        Dictionary<int, string> mpeg_type_lookup = new Dictionary<int, string>()
        {
            { 1, "MPEG1 Video" },
            { 3, "MPEG1 Audio"},
            { 15, "AAC Audio"},
            { 16, "H.263 Video"},
            { 27, "H.264 Video"},
            { 33, "JPEG2K Video"},
            { 36, "H.265 Video"},
            { 129, "AC3 Audio"}
        };

        #endregion
    }
}