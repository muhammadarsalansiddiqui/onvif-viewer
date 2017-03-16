﻿using RTSP_Viewer.OnvifPtzServiceReference;
using System;

namespace SDS.Video.Onvif
{
    //public enum PtzCommand
    //{
    //    PanEast,
    //    PanTiltNE,
    //    TiltNorth,
    //    PanTiltNW,
    //    PanWest,
    //    PanTiltSW,
    //    TiltSouth,
    //    PanTiltSE,
    //    ZoomIn,
    //    ZoomOut
    //}

    public class OnvifPtz
    {
        private System.Net.IPAddress IP;
        private int Port;
        private string User;
        private string Password;
        private PTZClient PtzClient;
        private RTSP_Viewer.OnvifMediaServiceReference.MediaClient MediaClient;
        public bool PtzAvailable;

        public OnvifPtz(string ip, int port)
        {
            System.Net.IPAddress.TryParse(ip, out IP);
            Port = port;
            PtzClient = OnvifServices.GetOnvifPTZClient(IP.ToString(), port);
            MediaClient = OnvifServices.GetOnvifMediaClient(IP.ToString(), Port);
            //IsPtz();
        }

        public OnvifPtz(string ip, int port, string user, string password)
        {
            System.Net.IPAddress.TryParse(ip, out IP);
            Port = port;
            User = user;
            Password = password;

            PtzClient = OnvifServices.GetOnvifPTZClient(IP.ToString(), Port, User, Password);
            MediaClient = OnvifServices.GetOnvifMediaClient(IP.ToString(), Port, User, Password);
        }

        /// <summary>
        /// Gets the first media profile that contains a PTZConfiguration from the the MediaClient GetProfiles command
        /// </summary>
        /// <returns>Media profile with PTZConfiguration</returns>
        private RTSP_Viewer.OnvifMediaServiceReference.Profile GetMediaProfile()
        {
            RTSP_Viewer.OnvifMediaServiceReference.Profile[] mediaProfiles = MediaClient.GetProfiles();

            foreach (RTSP_Viewer.OnvifMediaServiceReference.Profile p in mediaProfiles)
            {
                if (p.PTZConfiguration != null)
                    return MediaClient.GetProfile(p.token);
            }

            throw new Exception("No media profiles containing a PTZConfiguration on this device");
        }

        /// <summary>
        /// Pan the camera (uses the first media profile that is PTZ capable)
        /// </summary>
        /// <param name="speed">Percent of max speed to move the camera (1-100)</param>
        public void Pan(float speed)
        {
            RTSP_Viewer.OnvifMediaServiceReference.Profile mediaProfile = GetMediaProfile();
            PTZConfigurationOptions ptzConfigurationOptions = PtzClient.GetConfigurationOptions(mediaProfile.PTZConfiguration.token);

            PTZSpeed velocity = new PTZSpeed();
            velocity.PanTilt = new Vector2D() { x = speed * ptzConfigurationOptions.Spaces.ContinuousPanTiltVelocitySpace[0].XRange.Max, y = 0 };

            PtzClient.ContinuousMove(mediaProfile.token, velocity, null);
        }

        /// <summary>
        /// Tilt the camera (uses the first media profile that is PTZ capable)
        /// </summary>
        /// <param name="speed">Percent of max speed to move the camera (1-100)</param>
        public void Tilt(float speed)
        {
            RTSP_Viewer.OnvifMediaServiceReference.Profile mediaProfile = GetMediaProfile();
            PTZConfigurationOptions ptzConfigurationOptions = PtzClient.GetConfigurationOptions(mediaProfile.PTZConfiguration.token);

            PTZSpeed velocity = new PTZSpeed();
            velocity.PanTilt = new Vector2D() { x = 0, y = speed * ptzConfigurationOptions.Spaces.ContinuousPanTiltVelocitySpace[0].YRange.Max };

            PtzClient.ContinuousMove(mediaProfile.token, velocity, null);
        }

        /// <summary>
        /// Zoom the camera (uses the first media profile that is PTZ capable)
        /// </summary>
        /// <param name="speed">Percent of max speed to move the camera (1-100)</param>
        public void Zoom(float speed)
        {
            RTSP_Viewer.OnvifMediaServiceReference.Profile mediaProfile = GetMediaProfile();
            PTZConfigurationOptions ptzConfigurationOptions = PtzClient.GetConfigurationOptions(mediaProfile.PTZConfiguration.token);

            PTZSpeed velocity = new PTZSpeed();
            velocity.Zoom = new Vector1D() { x = speed * ptzConfigurationOptions.Spaces.ContinuousZoomVelocitySpace[0].XRange.Max };

            PtzClient.ContinuousMove(mediaProfile.token, velocity, null);
        }

        /// <summary>
        /// Combined Pan and Tilt of the camera (uses the first media profile that is PTZ capable)
        /// </summary>
        /// <param name="panSpeed">Percent of max speed to move the camera (0.01-1.00)</param>
        /// <param name="tiltSpeed">Percent of max speed to move the camera (0.01-1.00)</param>
        public void PanTilt(float panSpeed, float tiltSpeed)
        {
            RTSP_Viewer.OnvifMediaServiceReference.Profile mediaProfile = GetMediaProfile();
            PTZConfigurationOptions ptzConfigurationOptions = PtzClient.GetConfigurationOptions(mediaProfile.PTZConfiguration.token);

            PTZSpeed velocity = new PTZSpeed();
            velocity.PanTilt = new Vector2D() {
                x = panSpeed * ptzConfigurationOptions.Spaces.ContinuousPanTiltVelocitySpace[0].XRange.Max,
                y = tiltSpeed * ptzConfigurationOptions.Spaces.ContinuousPanTiltVelocitySpace[0].YRange.Max
            };

            PtzClient.ContinuousMove(mediaProfile.token, velocity, null);
        }

        /// <summary>
        /// Stop the camera (uses the first media profile that is PTZ capable).
        /// NOTE: may not work if not issued in conjunction with a move command
        /// </summary>
        public void Stop()
        {
            RTSP_Viewer.OnvifMediaServiceReference.Profile mediaProfile = GetMediaProfile();
            PtzClient.Stop(mediaProfile.token, true, true);
        }

        /// <summary>
        /// Move PTZ to provided preset number (defaults to media profile 0)
        /// </summary>
        /// <param name="presetNumber">Preset to use</param>
        public void ShowPreset(int presetNumber)
        {
            string presetToken = string.Empty;

            RTSP_Viewer.OnvifMediaServiceReference.Profile mediaProfile = GetMediaProfile();
            //Onvif_Interface.OnvifMediaServiceReference.Profile[] mediaProfiles = MediaClient.GetProfiles();
            string profileToken = mediaProfile.token;

            PTZPreset[] presets = PtzClient.GetPresets(profileToken);
            if (presets.Length >= presetNumber)
            {
                presetToken = presets[presetNumber - 1].token;

                PTZSpeed velocity = new PTZSpeed();
                velocity.PanTilt = new Vector2D() { x = (float)-0.5, y = 0 }; ;

                PtzClient.GotoPreset(profileToken, presetToken, velocity);
            }
            else
            {
                throw new Exception(string.Format("Invalid Preset requested - preset number {0}", presetNumber));
            }
        }

        /// <summary>
        /// *DON'T USE - not completed. Call up a preset by Profile/Preset token
        /// </summary>
        /// <param name="profileToken"></param>
        /// <param name="presetToken"></param>
        public void ShowPreset(string profileToken, string presetToken)
        {
            RTSP_Viewer.OnvifMediaServiceReference.Profile[] mediaProfiles = MediaClient.GetProfiles();
            profileToken = mediaProfiles[0].token;

            if (IsValidPresetToken(profileToken, presetToken))
            {
                PTZSpeed velocity = new PTZSpeed();
                velocity.PanTilt = new Vector2D() { x = (float)-0.5, y = 0 }; ;

                PtzClient.GotoPreset(profileToken, presetToken, velocity);
            }
            else
            {
                throw new Exception(string.Format("Invalid Preset requested - preset token {0}", presetToken));
            }
        }

        public bool IsValidPresetToken(string profileToken, string presetToken)
        {
            PTZPreset[] presets = PtzClient.GetPresets(profileToken);
            foreach (PTZPreset p in presets)
            {
                if (p.token == presetToken)
                    return true;
            }

            return false;
        }

        public PTZStatus GetPtzLocation()
        {
            RTSP_Viewer.OnvifMediaServiceReference.Profile mediaProfile = GetMediaProfile();

            PTZStatus status = PtzClient.GetStatus(mediaProfile.token);
            return status;
        }

        public bool IsPtz()
        {
            try
            {
                RTSP_Viewer.OnvifMediaServiceReference.Profile mediaProfile = GetMediaProfile();
                PtzAvailable = true;
            }
            catch (Exception ex)
            {
                PtzAvailable = false;
            }

            return PtzAvailable;
        }
    }
}