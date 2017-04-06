﻿using System.Collections.Generic;
using RTSP_Viewer.OnvifMediaServiceReference;
using RTSP_Viewer.OnvifDeviceManagementServiceReference;
using log4net;

namespace SDS.Video.Onvif
{
    public class OnvifCameraData
    {
        public Profile MediaProfile { get; set; }
        public Dictionary<string, string> ServiceUris { get; private set; } = new Dictionary<string, string>();
        //public List<string> StreamUris { get; private set; } = new List<string>();
        public string StreamUri { get; set; }
        public PTZConfiguration StreamPtzConfig { get { return MediaProfile.PTZConfiguration; } }

        public bool IsOnvifLoaded { get; private set; } = false;
        public bool IsPtz { get; private set; } = false;
        public bool IsPtzEnabled { get; private set; } = false;

        private static readonly ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public bool LoadOnvifData(string IP, int onvifPort, string user, string password, StreamType sType, TransportProtocol tProtocol, int streamIndex)  // Probably should be private and done automatically
        {
            GetOnvifUris(IP, 80, user, password);
            GetStreamUris(IP, 80, user, password, sType, tProtocol, streamIndex);
            IsOnvifLoaded = true;

            return true;
        }

        /// <summary>
        /// Retrieves Onvif service URIs from the device and stores them in the ServiceUris dictionary
        /// </summary>
        /// <param name="ip">IP Address</param>
        /// <param name="onvifPort">Port to connect on (normally HTTP - 80)</param>
        /// <param name="user">User name</param>
        /// <param name="password">User's Password</param>
        private void GetOnvifUris(string ip, int onvifPort, string user, string password)
        {
            ServiceUris.Clear();

            DeviceClient client = OnvifServices.GetOnvifDeviceClient(ip, onvifPort, user, password);
            Service[] svc = client.GetServices(IncludeCapability: false); // Bosch Autodome 800 response can't be deserialized if IncludeCapability enabled
            foreach (Service s in svc)
            {
                ServiceUris.Add(s.Namespace, s.XAddr);
            }

            // Check if this is an Onvif enabled PTZ
            if (ServiceUris.ContainsKey(OnvifNamespace.PTZ))
                IsPtz = true;
            else
                IsPtz = false;
        }

        /// <summary>
        /// Retrieves Onvif video stream URIs from the device and stores them in the StreamUris list
        /// </summary>
        /// <param name="onvifPort">Port to connect on (normally HTTP - 80)</param>
        private void GetStreamUris(string ip, int onvifPort, string user, string password, StreamType sType, TransportProtocol tProtocol, int StreamIndex)
        {
            //StreamUris.Clear();
            MediaClient mc = OnvifServices.GetOnvifMediaClient(ServiceUris[OnvifNamespace.MEDIA], user, password);
            Profile[] mediaProfiles = mc.GetProfiles();

            StreamSetup ss = new StreamSetup();
            Transport transport = new Transport() { Protocol = tProtocol };
            string uri = string.Empty;

            // Only store the Profile related to the StreamIndex from the XML file
            MediaProfile = mediaProfiles[StreamIndex - 1];

            // Get stream URI for the requested transport/protocol and insert the User/Password if present
            ss.Stream = sType;
            ss.Transport = transport;

            MediaUri mu = mc.GetStreamUri(ss, MediaProfile.token);
            if (user != "")
                uri = string.Format("{0}{1}:{2}@{3}", mu.Uri.Substring(0, mu.Uri.IndexOf("://") + 3), user, password, mu.Uri.Substring(mu.Uri.IndexOf("://") + 3));
            else
                uri = mu.Uri;

            StreamUri = uri;

            // A PTZ may not have a PTZ configuration for a particular media profile
            // Disable PTZ access in that case
            IsPtzEnabled = IsPtz;
            if (MediaProfile.PTZConfiguration == null && IsPtz)
            {
                log.Warn(string.Format("Disabling PTZ control based on the PTZConfiguration being null for stream profile {0}", StreamUri));
                IsPtzEnabled = false;
            }
        }

        // Example URI formats for various manufacturers (RTSP port = 554, uri = "rtsp://user:password@...")
        //  Bosch uri   = string.Format("{0}{1}:{2}/?h26x={3}&line={4}&inst={5}", uri, cam.IP, rtspPort, 4, cam.Device, cam.Stream);
        //  Axis uri    = string.Format("{0}{1}:{2}/onvif-media/media.amp", uri, cam.IP, rtspPort);
        //  Pelco uri   = string.Format("rtsp://{0}:{1}/stream{2}", cam.IP, rtspPort, cam.Stream);
        //  Samsung uri = string.Format("{0}{1}:{2}/onvif/profile{3}/media.smp", uri, cam.IP, rtspPort, cam.Stream);
    }
}
