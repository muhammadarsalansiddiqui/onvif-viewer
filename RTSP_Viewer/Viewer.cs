﻿using SDS.Utilities.IniFiles;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

using Vlc.DotNet.Forms;
using RTSP_Viewer.Classes;
using SDS.Video;
using log4net;
using System.Text.RegularExpressions;
using System.IO;
using System.ComponentModel;

namespace RTSP_Viewer
{
    public partial class Viewer : Form
    {
        private static readonly ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private int NumberOfViews;
        private const int ViewPadding = 1;
        //private NotifyIcon notification = new NotifyIcon() { Icon = SystemIcons.Application, Visible = true };

        // Create the HMI interface
        CallupsTxtFile hmi;

        VlcControl[] myVlcControl;
        VlcOverlay[] vlcOverlay;
        Panel statusBg = new Panel();
        private int ActiveViewer = 0;

        //OpcUaClient tagClient;
        IniFile MyIni;
        TextBox txtUri = new TextBox() { Tag = "Debug", Visible = false };
        ComboBox cbxViewSelect = new ComboBox() { Tag = "Debug", Visible = false };

        BackgroundWorker[] BgPtzWorker;

        public Viewer()
        {
            log4net.Config.XmlConfigurator.Configure(new System.IO.FileInfo("logger.xml"));
            log.Info("-------------------------");
            log.Info("Application Form loading");

            InitializeComponent();
            this.KeyPreview = true;
            this.FormClosing += Form1_FormClosing;
            this.KeyDown += Form1_KeyDown;

            InitializeForm();

            // Necessary for Samsung cameras.  The "Expect: 100-continue" HTTP header 
            // will prevent a connection to them (usually a 417 error will be reported)
            System.Net.ServicePointManager.Expect100Continue = false;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            this.Padding = new Padding(5);
            this.SizeChanged += Form1_ResizeEnd;

            OpcInterfaceInit();

            // This handles the size change that occurs after the Vlc controls initialize on startup
            setSizes();
            InitViewerStatus();

            // Initialize the HMI interface
            try
            {
                hmi = new CallupsTxtFile(new CallupsTxtFile.SetRtspCallupCallback(CameraCallup), getIniValue("CallupsFilePath"));
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format("Error starting application.  Unable to monitor callup file.\nApplication will now exit.\n\nException:\n{0}", ex.Message), "Startup failure", MessageBoxButtons.OK, MessageBoxIcon.Stop);
                Environment.Exit(1);
            }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            Cursor.Current = Cursors.WaitCursor;
            VlcViewer.DisconnectAll(myVlcControl);

            // Call disconnect (if tagClient is not null)
            //tagClient?.Disconnect();
            Cursor.Current = Cursors.Default;

            log.Info("Application Form closing");
        }

        private void InitializeForm()
        {
            log.Info("Initializing form");
            // Remove all controls and recreate
            this.Controls.Clear();

            MyIni = new IniFile();

            SetupVlc();
            InitViewerStatus();
            InitDebugControls();

            foreach (VlcOverlay vo in vlcOverlay)
            {
                // Make VlcControl fill overlay, associate each with the correct overlay, and add overlay to the form
                myVlcControl[vo.TabIndex].Dock = DockStyle.Fill;
                vo.Controls.Add(myVlcControl[vo.TabIndex]);
                this.Controls.Add(vo);
            }

            // Load values from ini file (default to stream 1 if none provided)
            int defaultStream = int.TryParse(getIniValue("DefaultStream"), out defaultStream) ? defaultStream : 1;
            string cameraFile = getIniValue("CameraFile");
            string cameraSchema = getIniValue("CameraSchemaFile");

            if (File.Exists(cameraFile) && File.Exists(cameraSchema))
            {
                // Load camera xml file and assign default mfgr if one not provided
                Camera.LoadAllCameraData("Bosch", defaultStream, cameraFile, cameraSchema);
            }
            else
            {
                log.Error(string.Format("CameraFile [{0}] and/or CameraSchemaFile [{1}] not found.", cameraFile, cameraSchema));
                MessageBox.Show(string.Format("Error starting application.  CameraFile '{0}' and/or CameraSchemaFile '{1}' not found.\nApplication will now exit.", cameraFile, cameraSchema), "Startup failure - Configuration files not found", MessageBoxButtons.OK, MessageBoxIcon.Stop);
                Environment.Exit(2);
            }
        }

        /// <summary>
        /// Configure the VLC Control(s) and overlay(s) that handle mouse events
        /// </summary>
        private void SetupVlc()
        {
            NumberOfViews = GetNumberOfViews();
            myVlcControl = new VlcControl[NumberOfViews];
            vlcOverlay = new VlcOverlay[NumberOfViews];
            string[] vlcMediaOptions = Regex.Split(getIniValue("VlcOptions"), "\\s*,\\s*"); // Split by comma and trim whitespace

            BgPtzWorker = new BackgroundWorker[NumberOfViews];

            for (int i = 0; i < NumberOfViews; i++)
            {
                BgPtzWorker[i] = new BackgroundWorker();
                BgPtzWorker[i].WorkerReportsProgress = true;
                BgPtzWorker[i].WorkerSupportsCancellation = true;
                BgPtzWorker[i].DoWork += BgPtzWorker_DoWork;

                myVlcControl[i] = new VlcControl();
                vlcOverlay[i] = new VlcOverlay() { Name = "VLC Overlay " + i, BackColor = Color.Transparent, TabIndex = i }; //, Parent = myVlcControl[i], Dock = DockStyle.Fill, TabIndex = i };
                vlcOverlay[i].GotoPtzPreset += Viewer_GotoPtzPreset;
                vlcOverlay[i].ToggleMute += Viewer_ToggleMute;

                // Add panel to VlcControl container to capture mouse events
                Panel MouseEventPanel = new Panel() { Parent = myVlcControl[i], BackColor = Color.Transparent, Dock = DockStyle.Fill, TabIndex = i, };
                MouseEventPanel.MouseEnter += VlcOverlay_MouseEnter;
                MouseEventPanel.MouseLeave += VlcOverlay_MouseLeave;
                MouseEventPanel.MouseDoubleClick += VlcOverlay_MouseDoubleClick;
                MouseEventPanel.MouseMove += VlcOverlay_MouseMove;
                MouseEventPanel.MouseDown += VlcOverlay_MouseDown;
                MouseEventPanel.MouseUp += VlcOverlay_MouseUp;
                MouseEventPanel.MouseWheel += VlcOverlay_MouseWheel;

                ((System.ComponentModel.ISupportInitialize)(myVlcControl[i])).BeginInit();

                myVlcControl[i].VlcLibDirectory = VlcViewer.GetVlcLibLocation();  // Tried to call once outside loop, but it causes in exception on program close
                myVlcControl[i].VlcMediaplayerOptions = vlcMediaOptions; // new string[] { "--network-caching=150", "--video-filter=deinterlace" };
                myVlcControl[i].Location = new Point(0, 0);
                myVlcControl[i].Name = string.Format("VLC Viewer {0}", i);
                myVlcControl[i].Rate = (float)0.0;
                myVlcControl[i].BackColor = Color.Gray;
                myVlcControl[i].TabIndex = i;
                //myVlcControl[i].MouseDoubleClick += VlcOverlay_MouseDoubleClick;

                // Events
                myVlcControl[i].Playing += OnVlcPlaying;
                myVlcControl[i].EncounteredError += MyVlcControl_EncounteredError;
                myVlcControl[i].Buffering += Form1_Buffering;

                //myVlcControl[i].Controls.Add(vlcOverlay[i]);
                // Had to add this line to make work
                ((System.ComponentModel.ISupportInitialize)(myVlcControl[i])).EndInit();
            }

            setSizes();
        }

        /// <summary>
        /// Creates the status object displayed in the lower right corner which
        /// shows the currently selected View and allows switching between views when a view is full screen
        /// </summary>
        private void InitViewerStatus()
        {
            // Only need this if showing more than 1 viewer
            if (NumberOfViews > 1)
            {
                statusBg.Controls.Clear();
                statusBg.BackColor = Color.Black;
                statusBg.Size = new Size(60, 37);
                statusBg.Location = new Point(this.ClientSize.Width - statusBg.Width - 10, this.ClientSize.Height - statusBg.Height - 10);
                statusBg.Anchor = (AnchorStyles.Bottom | AnchorStyles.Right);

                Panel[] viewer = new Panel[NumberOfViews];

                Point[] displayPoint = Utilities.CalculatePointLocations(NumberOfViews, statusBg.Width, statusBg.Height);
                Size displaySize = Utilities.CalculateItemSizes(NumberOfViews, statusBg.Size.Width, statusBg.Size.Height, 1); // ViewPadding);

                for (int i = 0; i < NumberOfViews; i++)
                {
                    viewer[i] = new Panel();
                    viewer[i].Location = displayPoint[i];
                    viewer[i].Size = displaySize;
                    viewer[i].BackColor = Color.Gainsboro;
                    viewer[i].Name = string.Format("Viewer Status {0}", i);
                    viewer[i].TabIndex = i;
                    viewer[i].MouseClick += ViewerStatus_MouseClick;
                    statusBg.Controls.Add(viewer[i]);
                }

                statusBg.Visible = true;
                this.Controls.Add(statusBg);
                statusBg.BringToFront();
            }
            else
            {
                statusBg.Visible = false;
            }
        }

        /// <summary>
        /// Update the viewer status object to set the active view
        /// </summary>
        /// <param name="activeView">Vlc View number to make active</param>
        private void SetViewerStatus(int activeView)
        {
            foreach (Control c in statusBg.Controls)
            {
                if (c.Name == string.Format("Viewer Status {0}", activeView))
                    c.BackColor = Color.Yellow;
                else
                    c.BackColor = Color.Gainsboro;
            }

            ActiveViewer = activeView;
        }

        private void InitDebugControls()
        {
            txtUri.Text = "rtsp://127.0.0.1:554/rtsp_tunnel?h26x=4&line=1&inst=1";
            txtUri.Location = new Point(10, this.Height - 60);
            txtUri.Width = 600;
            txtUri.Anchor = (AnchorStyles.Left | AnchorStyles.Bottom);
            this.Controls.Add(txtUri);

            Button playBtn = new Button() { Tag = "Debug", Visible = false };
            playBtn.Text = "Connect";
            playBtn.Location = new Point(10, txtUri.Top - txtUri.Height - 10);
            playBtn.Anchor = (AnchorStyles.Left | AnchorStyles.Bottom);
            playBtn.Click += PlayBtn_Click;
            this.Controls.Add(playBtn);

            Button pauseBtn = new Button() { Tag = "Debug", Visible = false };
            pauseBtn.Text = "Pause";
            pauseBtn.Location = new Point(playBtn.Right + 20, txtUri.Top - txtUri.Height - 10);
            pauseBtn.Anchor = (AnchorStyles.Left | AnchorStyles.Bottom);
            pauseBtn.Click += PauseBtn_Click;
            this.Controls.Add(pauseBtn);

            Button stopBtn = new Button() { Tag = "Debug", Visible = false };
            stopBtn.Text = "Disconnect";
            stopBtn.Location = new Point(pauseBtn.Right + 20, txtUri.Top - txtUri.Height - 10);
            stopBtn.Anchor = (AnchorStyles.Left | AnchorStyles.Bottom);
            stopBtn.Click += StopBtn_Click;
            this.Controls.Add(stopBtn);

            cbxViewSelect.Location = new Point(stopBtn.Right + 20, stopBtn.Top);
            cbxViewSelect.Anchor = (AnchorStyles.Left | AnchorStyles.Bottom);
            cbxViewSelect.Width = 100;
            cbxViewSelect.Height = playBtn.Height;
            for (int i = 0; i < NumberOfViews; i++)
            {
                cbxViewSelect.Items.Add(string.Format("Viewer {0}", i + 1));
            }
            cbxViewSelect.SelectedIndex = 0;
            this.Controls.Add(cbxViewSelect);

            Button btnLoadLast = new Button() { Tag = "Debug", Visible = false };
            btnLoadLast.Text = "Load Last";
            btnLoadLast.Location = new Point(cbxViewSelect.Right + 20, txtUri.Top - txtUri.Height - 10);
            btnLoadLast.Anchor = (AnchorStyles.Left | AnchorStyles.Bottom);
            btnLoadLast.Click += BtnLoadLast_Click; ;
            this.Controls.Add(btnLoadLast);
        }

        /// <summary>
        /// Establish Opc connection (if enabled) in own thread 
        /// </summary>
        private void OpcInterfaceInit()
        {
            int opcEnable = 0;
            // Read value from ini if present
            Int32.TryParse(getIniValue("OPC_Interface_Enable"), out opcEnable);

            if (opcEnable > 0)
            {
                // Instantiate OPC client and provide delegate function to handle callups
                //tagClient = new OpcUaClient(CameraCallup);

                // OPC server and path to subscribe to
                string endPointURL = getIniValue("OPC_Endpoint_URL");
                string tagPath = getIniValue("OPC_Tag_Path");

                // Establish Opc connection/subscription on own thread
                log.Info("Initializing OPC connection");
                //tagClient.StartInterface(endPointURL, tagPath);
            }
            else
            {
                log.Info("OPC disabled in ini file");
            }
        }

        public int GetNumberOfViews()
        {
            int views = 0;

            // Read value from ini if present, otherwise default to 1 view and write to ini
            if (!Int32.TryParse(getIniValue("NumberOfViews"), out views))
            {
                views = 1;
                MyIni.Write("NumberOfViews", views.ToString());
            }

            // Force the Number of views to be a power of 2 (1, 2, 4, 16, etc)
            var sqrtInt = Math.Truncate(Math.Sqrt(Convert.ToDouble(views)));
            double sqrt = Convert.ToDouble(sqrtInt);
            views = Convert.ToInt32(Math.Pow(sqrt, Convert.ToDouble(2)));
            return views;
        }

        /// <summary>
        /// Set the size/postion of each VLC control based on the total number
        /// </summary>
        public void setSizes()
        {
            log.Info(string.Format("Display normal layout ({0} views)", NumberOfViews));
            SuspendLayout();

            Point[] displayPoint = Utilities.CalculatePointLocations(NumberOfViews, ClientSize.Width, ClientSize.Height);
            Size displaySize = Utilities.CalculateItemSizes(NumberOfViews, ClientSize.Width, ClientSize.Height, ViewPadding);

            for (int i = 0; i < NumberOfViews; i++)
            {
                vlcOverlay[i].Location = displayPoint[i];
                vlcOverlay[i].Size = displaySize;
            }

            ResumeLayout();
        }

        /// <summary>
        /// Open the provided URI on the provide VLC position
        /// </summary>
        /// <param name="URI">URI to open</param>
        /// <param name="ViewerNum">VLC control to display video on</param>
        private void CameraCallup(Uri URI, int ViewerNum)
        {
            log.Debug(string.Format("Camera callup for view {0} [{1}]", ViewerNum, URI));
            if (ViewerNum >= 0)
            {
                vlcOverlay[ViewerNum].ShowNotification("Loading...");
                vlcOverlay[ViewerNum].ShowMuteButton(false);

                myVlcControl[ViewerNum].Play(URI, "");
                myVlcControl[ViewerNum].BackColor = Color.Black;
                Debug.Print(myVlcControl[ViewerNum].State.ToString());
                myVlcControl[ViewerNum].UseWaitCursor = true;

                // Store the URI in the ini file
                MyIni.Write("lastURI", URI.AbsoluteUri, "Viewer_" + ViewerNum);
                vlcOverlay[ViewerNum].LastCamUri = URI.AbsoluteUri;
            }
        }

        /// <summary>
        /// Callup the requested camera on the provided display number (preset not implemented) and enable PTZ controls if available
        /// </summary>
        /// <param name="ViewerNum">Control to display video on</param>
        /// <param name="CameraNum">Camera number to display</param>
        /// <param name="Preset">Camera Preset</param>
        private void CameraCallup(int ViewerNum, int CameraNum, int Preset)
        {
            Camera cam = null;

            try
            {
                vlcOverlay[ViewerNum].ShowNotification("Getting stream");

                // Get the Onvif stream URI and callup the camera
                cam = Camera.GetCamera(CameraNum);
                Uri URI = cam.GetCameraUri(OnvifMediaServiceReference.TransportProtocol.RTSP, OnvifMediaServiceReference.StreamType.RTPUnicast);

                // Try multicast URI if available
                if (cam.OnvifData.MulticastUri != null)
                    CameraCallup(cam.OnvifData.MulticastUri, ViewerNum);
                else
                    CameraCallup(URI, ViewerNum);
            }
            catch (Exception ex)
            {
                log.Error(string.Format("Unable to callup camera.  Exception: {0}", ex.Message));

                myVlcControl[ViewerNum].Stop();
                myVlcControl[ViewerNum].BackColor = Color.Gray;

                string status = string.Format("Camera #{0} unavailable", CameraNum);
                vlcOverlay[ViewerNum].ShowNotification(status);
                throw;
            }

            // Prepare PTZ object, enable the PTZ functionality on the Overlay if available, and go to preset
            if (cam.IsPtz && cam.IsPtzEnabled)
            {
                vlcOverlay[ViewerNum].PtzController = cam.PtzController;
                
                if (Preset > 0)
                    try
                    {
                        vlcOverlay[ViewerNum].PtzController.ShowPreset(Preset);
                    }
                    catch (IndexOutOfRangeException)
                    {
                        vlcOverlay[ViewerNum].ShowNotification(string.Format("Preset #{0} undefined", Preset), 3000);
                    }
            }

            vlcOverlay[ViewerNum].EnablePtzPresets(cam.IsPtzEnabled, cam.PtzController.GetPresetCount());
        }

        private void PtzStop(VlcOverlay overlay)
        {
            // Stop PTZ if moving
            Debug.Print(string.Format("{0} Stop PTZ if necessary ({1})", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), overlay.Name));

            // Check if PTZ and enable PTZ controls if necessary
            if (overlay.PtzEnabled && overlay.PtzController != null && overlay.PtzController.PtzMoving)
            {
                log.Debug(string.Format("Camera stopping on view {0} [{1}]", overlay.Name, overlay.LastCamUri));
                Debug.Print(string.Format("Camera stopping on view {0} [{1}]", overlay.Name, overlay.LastCamUri));
                overlay.PtzController.Stop();
            }
        }

        private void SetVlcFullView(int viewerIndex)
        {
            log.Info(string.Format("Display full screen layout (View #{0})", viewerIndex));
            foreach (VlcOverlay vlc in vlcOverlay)
            {
                if (vlc.TabIndex == viewerIndex)
                {
                    VlcOverlay.SetFullView(this, vlc);
                    statusBg.Visible = true;
                    statusBg.BringToFront();
                    SetViewerStatus(viewerIndex);
                    break;
                }
            }
        }

        /// <summary>
        /// Read a key value from an Ini file
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        private string getIniValue(string key)
        {
            try
            {
                var value = MyIni.Read(key);

                if (!MyIni.KeyExists(key))
                {
                    // This guarantees that an Ini file will be created if it doesn't exist
                    MyIni.Write(key, value);
                }

                return value;
            }
            catch
            {
                log.Warn(string.Format("Error reading value for ini key [{0}]", key));
                throw new Exception(string.Format("Error reading value for ini key [{0}]", key));
            }
        }

        private void PlayBtn_Click(object sender, EventArgs e)
        {
            CameraCallup(new Uri(this.txtUri.Text), cbxViewSelect.SelectedIndex);
            vlcOverlay[cbxViewSelect.SelectedIndex].EnablePtzPresets(true); // Temporary for testing
        }

        private void BtnLoadLast_Click(object sender, EventArgs e)
        {
            VlcViewer.loadLastStream(myVlcControl, MyIni);
        }

        private void PauseBtn_Click(object sender, EventArgs e)
        {
            int viewerNum = cbxViewSelect.SelectedIndex;
            VlcViewer.TogglePause(myVlcControl[viewerNum]);
        }

        private void StopBtn_Click(object sender, EventArgs e)
        {
            int viewerNum = cbxViewSelect.SelectedIndex;
            if (viewerNum >= 0)
            {
                myVlcControl[viewerNum].Stop();
                myVlcControl[viewerNum].BackColor = Color.Gray;
            }
        }

        private void Viewer_GotoPtzPreset(object sender, PresetEventArgs e)
        {
            Panel pan = (Panel)sender;
            VlcOverlay overlay = vlcOverlay[pan.TabIndex];

            // Check if PTZ and enable PTZ controls if necessary
            if (overlay.PtzEnabled)
            {
                if (overlay.PtzController == null)
                {
                    log.Warn(string.Format("No PtzController configured for camera stream [{0}]", overlay.LastCamUri));
                    throw new Exception(string.Format("No PtzController configured for camera stream [{0}]", overlay.LastCamUri));
                }

                try
                {
                    overlay.PtzController.ShowPreset(e.Preset);
                }
                catch (IndexOutOfRangeException)
                {
                    overlay.ShowNotification(string.Format("Preset #{0} undefined", e.Preset), 3000);
                }
            }
        }

        private void Viewer_ToggleMute(object sender, EventArgs e)
        {
            VlcOverlay overlay = (VlcOverlay)sender;
            VlcControl vlc = myVlcControl[overlay.TabIndex];
            if (vlc?.Audio != null)
            {
                vlc.Audio.ToggleMute();
            }

            overlay.SetMuteState(vlc.Audio.IsMute);
        }

        private void VlcOverlay_MouseDoubleClick(object sender, EventArgs e)
        {
            Panel overlay = (Panel)sender;
            VlcControl vlc = myVlcControl[overlay.TabIndex]; // (VlcControl)overlay.Parent;
            this.SuspendLayout();
            if (vlc.Width >= this.ClientSize.Width)
            {
                setSizes();
                vlc.SendToBack();
            }
            else
            {
                VlcOverlay.SetFullView(this, vlcOverlay[vlc.TabIndex]);
                statusBg.Visible = true;
                statusBg.BringToFront();
            }
            this.ResumeLayout();
        }

        private void VlcOverlay_MouseEnter(object sender, EventArgs e)
        {
            // Select control so the mouse wheel event will go to the proper control
            Panel p = (Panel)sender;
            VlcOverlay overlay = vlcOverlay[p.TabIndex]; // (VlcOverlay)sender;
            p.Select();

            log.Debug(string.Format("Mouse entered view {0}", overlay.Name));

            if (!overlay.PtzEnabled | !myVlcControl[overlay.TabIndex].IsPlaying)
            {
                // Disable PTZ actions if not playing
                overlay.PtzEnabled = false;
                this.Cursor = Cursors.Default;
            }

            ActiveViewer = overlay.TabIndex;
        }

        private void VlcOverlay_MouseLeave(object sender, EventArgs e)
        {
            // This is a terrible way to make sure the PTZ stops - replace with better solution
            Panel p = (Panel)sender;
            VlcOverlay overlay = vlcOverlay[p.TabIndex]; // (VlcOverlay)sender;
            log.Info(string.Format("Mouse exited view {0} [NOTE: REPLACE PTZ STOP ON EXIT WITH BETTER SOLUTION]", overlay.Name));
            PtzStop(overlay);
        }

        private void VlcOverlay_MouseWheel(object sender, MouseEventArgs e)
        {
            Panel p = (Panel)sender;
            VlcOverlay overlay = vlcOverlay[p.TabIndex];

            // Have overlay process mouse change
            overlay.SetZoomSpeed(e);

            // Use BackgroundWorker to send command to prevent UI lockup
            if (!BgPtzWorker[overlay.TabIndex].IsBusy)
            {
                object[] args = new object[] { overlay, e };
                BgPtzWorker[overlay.TabIndex].RunWorkerAsync(args);
            }
            else
            {
                //log.Debug(string.Format("Background worker busy.  Ignoring mouse wheel for view {0} [{1}]", overlay.Name, overlay.LastCamUri));
            }
        }

        private void VlcOverlay_MouseDown(object sender, MouseEventArgs e)
        {
            Panel p = (Panel)sender;
            VlcOverlay overlay = vlcOverlay[p.TabIndex]; // (VlcOverlay)sender;
            Debug.Print(string.Format("{0} Mouse down ({1})", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), overlay.Name));
            log.Debug(string.Format("Mouse down on view {0}", overlay.Name));

            // Use BackgroundWorker to send command to prevent UI lockup
            if (!BgPtzWorker[overlay.TabIndex].IsBusy)
            {
                object[] args = new object[] { overlay, e };
                BgPtzWorker[overlay.TabIndex].RunWorkerAsync(args);
            }
            else
            {
                log.Debug(string.Format("Background worker busy.  Ignoring mouse down for view {0} [{1}]", overlay.Name, overlay.LastCamUri));
            }
        }

        private void VlcOverlay_MouseUp(object sender, MouseEventArgs e)
        {
            Panel p = (Panel)sender;
            VlcOverlay overlay = vlcOverlay[p.TabIndex]; // (VlcOverlay)sender;
            log.Debug(string.Format("Mouse up on view {0}", overlay.Name));

            // Set the viewer status
            cbxViewSelect.SelectedIndex = p.TabIndex;
            SetViewerStatus(p.TabIndex);
            txtUri.Text = MyIni.Read("lastURI", "Viewer_" + p.TabIndex);

            if (e.Button == MouseButtons.Right)
            {
                VlcViewer.TogglePause(myVlcControl[p.TabIndex]);
            }

            // Attempt to prevent unstopping PTZ (stop sent before PTZ?)
            BgPtzWorker[overlay.TabIndex].CancelAsync();
            PtzStop(overlay);
        }

        /// <summary>
        /// Sends PTZ commands to the relevant camera
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e">Object containing the relevant Vlc View overlay and the mouse event args</param>
        private void BgPtzWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            object[] args = e.Argument as object[];

            VlcOverlay overlay = (VlcOverlay)args[0];
            MouseEventArgs mouseArgs = (MouseEventArgs)args[1];

            if (!myVlcControl[overlay.TabIndex].IsPlaying)
            {
                Debug.Print(string.Format("{0} VLC not playing.  No PTZ command sent.", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")));
                //log.Debug(string.Format("VLC not playing.  No PTZ command sent to view {0}", overlay.Name));
                return;
            }

            // Check if PTZ and enable PTZ controls if necessary
            if (overlay.PtzEnabled)
            {
                if (overlay.PtzController == null)
                {
                    log.Warn(string.Format("No PtzController configured for camera stream [{0}]", overlay.LastCamUri));
                    throw new Exception(string.Format("No PtzController configured for camera stream [{0}]", overlay.LastCamUri));
                }

                if (mouseArgs.Delta != 0)
                {
                    // Initiate continuous move zoom.  Stopped by ScrollTimer Elapsed event
                    overlay.PtzController.Zoom(overlay.ScrollSpeed);
                }
                else
                {
                    // Calculate the speed Pan and Tilt using the mouse location
                    // Uses the center of the control as point 0, 0 (i.e the center)
                    // A negative pan speed moves the camera to the left, positive to the right
                    // A negative tilt speed moves the camera down, positive moves it up
                    // The speed is a value between 0 and 1 (represents a percent of max speed)
                    float panSpeed = (float)(mouseArgs.X - (overlay.Width / 2)) / (float)(overlay.Width / 2);
                    float tiltSpeed = (float)((overlay.Height / 2) - mouseArgs.Y) / (float)(overlay.Height / 2);

                    log.Debug(string.Format("Sending PTZ Command to move [Pan Speed: {0}, Tilt Speed: {1}] on view {2} [{3}]", panSpeed, tiltSpeed, overlay.Name, overlay.LastCamUri));
                    overlay.PtzController.PanTilt(panSpeed, tiltSpeed);
                }
            }
        }

        private void VlcOverlay_MouseMove(object sender, MouseEventArgs e)
        {
            Panel p = (Panel)sender;
            VlcOverlay overlay = vlcOverlay[p.TabIndex]; // (VlcOverlay)sender;

            int minMovePercent = 3;
            if (overlay.LastMouseArgs == null)
                overlay.LastMouseArgs = e;

            int x = overlay.Size.Width / 2;
            int y = overlay.Size.Height / 2;
            //string quadrant = "";

            int deltaX = e.X - x;
            int deltaY = y - e.Y;

            //float radius = (float)Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
            double angle = Math.Atan2(deltaY, deltaX) * (180 / Math.PI);

            //if (deltaY >= 0)
            //    quadrant = "Top";
            //else
            //    quadrant = "Bottom";

            //if (deltaX >= 0)
            //    quadrant += " Right";
            //else
            //    quadrant += " Left";

            if (overlay.PtzEnabled)
                this.Cursor = Utilities.GetPtzCursor(angle);

            //Invoke((Action)(() => { overlay.Controls["Status"].Text = string.Format("{0}\nMouse @ ({1}, {2})\nPolar: {3:0.#}@{4:0.##}\nCart.: {5},{6}", quadrant, e.Location.X, e.Location.Y, radius, angle, deltaX, deltaY); overlay.Controls["Status"].Visible = true; }));

            // Change PTZ command based on mouse position (only if left button down)
            if (e.Button == MouseButtons.Left)
            {
                //Debug.Print(string.Format("Mouse Move with button {0} pressed @ {1}, {2}", e.Button, e.X, e.Y));

                if (Math.Abs((overlay.LastMouseArgs.X - e.X)) > (overlay.Width * ((float)minMovePercent / 100)))
                {
                    Debug.Print(string.Format("{0}           {1}", Math.Abs((overlay.LastMouseArgs.X - e.X)), (overlay.Width * ((float)minMovePercent / 100))));
                    Debug.Print(string.Format("Mouse moved horizontally by more than the minimum percentage [{0}] to [{1}, {2}]", minMovePercent, e.X, e.Y));
                }
                else if (Math.Abs((overlay.LastMouseArgs.Y - e.Y)) > (overlay.Height * ((float)minMovePercent / 100)))
                {
                    Debug.Print(string.Format("{0}           {1}", Math.Abs((overlay.LastMouseArgs.Y - e.Y)), (overlay.Height * ((float)minMovePercent / 100))));
                    Debug.Print(string.Format("Mouse moved vertically by more than the minimum percentage [{0}] to [{1}, {2}]", minMovePercent, e.X, e.Y));
                }
                else
                {
                    return;
                }

                // Use BackgroundWorker to send command to prevent UI lockup
                if (!BgPtzWorker[overlay.TabIndex].IsBusy)
                {
                    // Only store new mouse position if a command is successfully sent
                    // Otherwise an attempt to send the command should be made the next time the mouse moves
                    overlay.LastMouseArgs = e;
                    object[] args = new object[] { overlay, e };
                    BgPtzWorker[overlay.TabIndex].RunWorkerAsync(args);
                }
                else
                {
                    //log.Debug(string.Format("Background worker busy.  Ignoring mouse down for view {0} [{1}]", overlay.Name, overlay.LastCamUri));
                }
            }
            else if (e.Button == MouseButtons.None)
            {
                // Allow some mouse movement (hard to use scroll wheel with no change in mouse position)
                if (Math.Abs((overlay.LastMouseArgs.X - e.X)) > (overlay.Width * ((float)minMovePercent / 100)) |
                    (Math.Abs((overlay.LastMouseArgs.Y - e.Y)) > (overlay.Height * ((float)minMovePercent / 100))))
                {
                    // PtzMoving should be false if no buttons are pressed.  
                    // This is to help prevent the Ptz from moving continuously as 
                    // it does sometime if the mouse up is not detected or the stop is sent before the PTZ command
                    //  Should become unecessary when a real fix for those issues is implemented
                    if (overlay.PtzEnabled && overlay.PtzController != null && overlay.PtzController.PtzMoving)
                        PtzStop(overlay);
                }
            }
        }

        private void MyVlcControl_EncounteredError(object sender, Vlc.DotNet.Core.VlcMediaPlayerEncounteredErrorEventArgs e)
        {
            VlcControl vlc = (VlcControl)sender;
            vlcOverlay[int.Parse(vlc.Name.Split()[2])].ShowNotification("Error");

            log.Error(string.Format("Error encountered on '{0}': {1}", vlc.Name, e.ToString()));

            //MessageBox.Show(string.Format("Error encountered on '{0}':\n{1}", vlc.Name, e.ToString()), "VLC Control Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            vlc.UseWaitCursor = false;
        }

        private void OnVlcPlaying(object sender, Vlc.DotNet.Core.VlcMediaPlayerPlayingEventArgs e)
        {
            VlcControl vlc = (VlcControl)sender;
            vlc.UseWaitCursor = false;

            vlcOverlay[vlc.TabIndex].HideNotification();

            var mediaInformations = vlc.GetCurrentMedia().TracksInformations;
            foreach (var mediaInformation in mediaInformations)
            {
                if (mediaInformation.Type == Vlc.DotNet.Core.Interops.Signatures.MediaTrackTypes.Audio)
                {
                    log.Debug(string.Format("{0} Audio info - Codec: {1}, Channels: {2}, Rate: {3}", vlc.Name, mediaInformation.CodecName, mediaInformation.Audio.Channels, mediaInformation.Audio.Rate));
                    vlcOverlay[vlc.TabIndex].ShowNotification("Audio stream active", 5000);
                    vlcOverlay[vlc.TabIndex].ShowMuteButton(true);
                }
                else if (mediaInformation.Type == Vlc.DotNet.Core.Interops.Signatures.MediaTrackTypes.Video)
                {
                    log.Debug(string.Format("{0} Video info - Codec: {1}, Height: {2}, Width: {3}", vlc.Name, mediaInformation.CodecName, mediaInformation.Video.Height, mediaInformation.Video.Width));
                }
            }
        }

        private void Form1_Buffering(object sender, Vlc.DotNet.Core.VlcMediaPlayerBufferingEventArgs e)
        {
            VlcControl vlc = (VlcControl)sender;

            Console.WriteLine(string.Format("{0}\tBuffering: {1}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), vlc.Name));
        }

        private void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.KeyData)
            {
                case Keys.F5:
                    Cursor.Current = Cursors.WaitCursor;
                    VlcViewer.DisconnectAll(myVlcControl);
                    InitializeForm();
                    Cursor.Current = Cursors.Default;
                    break;

                case Keys.F11:
                    if (WindowState != FormWindowState.Maximized)
                    {
                        // Change border style first or else the ClientSize will be larger than the screen dimensions
                        FormBorderStyle = FormBorderStyle.None;
                        WindowState = FormWindowState.Maximized;
                    }
                    else
                    {
                        WindowState = FormWindowState.Normal;
                        FormBorderStyle = FormBorderStyle.Sizable;
                    }
                    break;

                case Keys.Control | Keys.D:
                    foreach (Control c in this.Controls)
                    {
                        if (c.Tag?.ToString() == "Debug")
                        {
                            c.Visible = !c.Visible;
                            c.BringToFront();
                        }
                    }
                    break;
            }

            // Call preset if number key pressed
            if (char.IsDigit((char)e.KeyCode) && vlcOverlay[ActiveViewer].PtzController != null)
            {
                int preset = (int)e.KeyValue - (int)Keys.D0;
                if (preset > 0)
                {
                    vlcOverlay[ActiveViewer].ShowNotification(string.Format("Preset {0}", preset), 2000);
                    try
                    {
                        vlcOverlay[ActiveViewer].PtzController.ShowPreset(preset);
                    }
                    catch (IndexOutOfRangeException)
                    {
                        vlcOverlay[ActiveViewer].ShowNotification(string.Format("Preset #{0} undefined", preset), 3000);
                    }
                }
            }
        }

        private void Form1_ResizeEnd(object sender, EventArgs e)
        {
            // Adjust size and position of VLC controls to match new form size
            setSizes();
        }

        private void ViewerStatus_MouseClick(object sender, MouseEventArgs e)
        {
            Panel view = (Panel)sender;

            // Switch to this view if in full screen view
            foreach (VlcControl vc in myVlcControl)
            {
                if (vc.Width >= this.ClientSize.Width)
                {
                    if (view.TabIndex != vc.TabIndex)
                    {
                        // Switch which Vlc control is full screen
                        setSizes(); //Temporary hack
                        vc.SendToBack();
                        SetVlcFullView(view.TabIndex);
                    }
                    break;
                }
            }
        }
    }
}
