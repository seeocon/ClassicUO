﻿#define DEV_BUILD

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Input;
using ClassicUO.IO;
using ClassicUO.Network;
using ClassicUO.Renderer;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using ClassicUO.Utility.Platforms;

using ImGuiNET;

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

using SDL2;
using static SDL2.SDL;

using Vector2 = System.Numerics.Vector2;
using Vector3 = Microsoft.Xna.Framework.Vector3;

namespace ClassicUO
{
    class GameController : Microsoft.Xna.Framework.Game
    {
        private Scene _scene;
        private bool _dragStarted;
        private bool _ignoreNextTextInput;
        private readonly GraphicsDeviceManager _graphicDeviceManager;
        private readonly UltimaBatcher2D _uoSpriteBatch;
        private readonly float[] _intervalFixedUpdate = new float[2];
        private double _statisticsTimer;
        private ImGuiRenderer _imGuiRenderer;
        private IntPtr _bufferPtr;


        public GameController()
        {
            _graphicDeviceManager = new GraphicsDeviceManager(this);
            _uoSpriteBatch = new UltimaBatcher2D(GraphicsDevice);
            _imGuiRenderer = new ImGuiRenderer(this);
        }

        public Scene Scene => _scene;
        public uint[] FrameDelay { get; } = new uint[2];

        public T GetScene<T>() where T : Scene
        {
            return _scene as T;
        }


        protected override void Initialize()
        {
            Log.Trace("Setup GraphicDeviceManager");

            _graphicDeviceManager.PreparingDeviceSettings += (sender, e) => e.GraphicsDeviceInformation.PresentationParameters.RenderTargetUsage = RenderTargetUsage.DiscardContents;
            if (_graphicDeviceManager.GraphicsDevice.Adapter.IsProfileSupported(GraphicsProfile.HiDef))
                _graphicDeviceManager.GraphicsProfile = GraphicsProfile.HiDef;

            _graphicDeviceManager.PreferredDepthStencilFormat = DepthFormat.Depth24Stencil8;
            _graphicDeviceManager.SynchronizeWithVerticalRetrace = false; // TODO: V-Sync option
            _graphicDeviceManager.ApplyChanges();

            Window.ClientSizeChanged += WindowOnClientSizeChanged;
            Window.AllowUserResizing = true;
            Window.Title = $"ClassicUO - {CUOEnviroment.Version}";
            IsMouseVisible = Settings.GlobalSettings.RunMouseInASeparateThread;

            IsFixedTimeStep = false; // Settings.GlobalSettings.FixedTimeStep;
            TargetElapsedTime = TimeSpan.FromMilliseconds(1000.0f / 250);

            SetRefreshRate(Settings.GlobalSettings.FPS);

            ImGui.GetIO().Fonts.AddFontDefault();
            _imGuiRenderer.RebuildFontAtlas();
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1);
            //ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0);

            _buffer = new RenderTarget2D(GraphicsDevice, _graphicDeviceManager.PreferredBackBufferWidth, _graphicDeviceManager.PreferredBackBufferHeight, false, SurfaceFormat.Color, DepthFormat.Depth24Stencil8);
            _bufferPtr = _imGuiRenderer.BindTexture(_buffer);

            base.Initialize();
        }

        protected override void LoadContent()
        {
            LoadGameFilesFromFileSystem();
            base.LoadContent();

            SetScene(new LoginScene());
        }

        protected override void UnloadContent()
        {
            SDL_GetWindowBordersSize(Window.Handle, out int top, out int left, out _, out _);
            Settings.GlobalSettings.WindowPosition = new Point( Math.Max(0, Window.ClientBounds.X - left),
                                                               Math.Max(0, Window.ClientBounds.Y - top));
            _scene?.Unload();
            Settings.GlobalSettings.Save();
            Plugin.OnClosing();
            base.UnloadContent();
        }

        public void SetScene(Scene scene)
        {
            _scene?.Dispose();
            _scene = scene;

            if (scene != null)
            {
                Window.AllowUserResizing = scene.CanResize;


                if (scene.CanBeMaximized)
                {
                    SetWindowSize(scene.Width, scene.Height);
                    MaximizeWindow();
                }
                else
                {
                    RestoreWindow();
                    SetWindowSize(scene.Width, scene.Height);
                    SDL_GetWindowBordersSize(Window.Handle, out int top, out int left, out int bottom, out int right);

                    if (Settings.GlobalSettings.WindowPosition.HasValue)
                    {
                        SetWindowPosition(left + Settings.GlobalSettings.WindowPosition.Value.X, top + Settings.GlobalSettings.WindowPosition.Value.Y);
                    }
                }

                scene.Load();
            }
        }

        public void SetRefreshRate(int rate)
        {
            if (rate < Constants.MIN_FPS)
                rate = Constants.MIN_FPS;
            else if (rate > Constants.MAX_FPS)
                rate = Constants.MAX_FPS;

            FrameDelay[0] = FrameDelay[1] = (uint) (1000 / rate);
            FrameDelay[1] = FrameDelay[1] >> 1;

            Settings.GlobalSettings.FPS = rate;
            //TargetElapsedTime = TimeSpan.FromMilliseconds(1000.0f / 250);

            _intervalFixedUpdate[0] = 1000.0f / rate;
            _intervalFixedUpdate[1] = 217;  // 5 FPS
        }

        public void SetWindowPosition(int x, int y)
        {
            SDL.SDL_SetWindowPosition(Window.Handle, x, y);
        }

        public void SetWindowSize(int width, int height)
        {
            _graphicDeviceManager.PreferredBackBufferWidth = width;
            _graphicDeviceManager.PreferredBackBufferHeight = height;
            _graphicDeviceManager.ApplyChanges();

            _buffer?.Dispose();
            _buffer = new RenderTarget2D(GraphicsDevice, _graphicDeviceManager.PreferredBackBufferWidth, _graphicDeviceManager.PreferredBackBufferHeight, false, SurfaceFormat.Color, DepthFormat.Depth24Stencil8);

            var io = ImGui.GetIO();
            io.DisplaySize = new Vector2(width, height);
            io.DisplayFramebufferScale = Vector2.One;
        }

        public void SetWindowBorderless(bool borderless)
        {
            SDL_SetWindowBordered(Window.Handle, borderless ? SDL_bool.SDL_FALSE : SDL_bool.SDL_TRUE);

            SDL_GetCurrentDisplayMode(0, out SDL_DisplayMode displayMode);

            int width = displayMode.w;
            int height = displayMode.h;

            if (borderless)
            {
                SetWindowSize(width, height);
                SDL_SetWindowPosition(Window.Handle, 0, 0);
            }
            else
            {
                int top, left, bottom, right;
                SDL_GetWindowBordersSize(Window.Handle, out top, out left, out bottom, out right);
                SetWindowSize(width, height - (top - bottom));
                SDL_SetWindowPosition(Window.Handle, 0, top - bottom);
            }

            var viewport = UIManager.GetGump<WorldViewportGump>();

            if (viewport != null && ProfileManager.Current.GameWindowFullSize)
            {
                viewport.ResizeGameWindow(new Point(width, height));
                viewport.X = -5;
                viewport.Y = -5;
            }
        }

        public void MaximizeWindow()
        {
            SDL.SDL_MaximizeWindow(Window.Handle);
        }

        public void RestoreWindow()
        {
            SDL.SDL_RestoreWindow(Window.Handle);
        }

        public void LoadGameFilesFromFileSystem()
        {
            Log.Trace( "Checking for Ultima Online installation...");
            Log.PushIndent();


            try
            {
                FileManager.UoFolderPath = Settings.GlobalSettings.UltimaOnlineDirectory;
            }
            catch (FileNotFoundException)
            {
                Log.Error( "Wrong Ultima Online installation folder.");

                throw;
            }

            Log.Trace( "Done!");
            Log.Trace( $"Ultima Online installation folder: {FileManager.UoFolderPath}");
            Log.PopIndent();

            Log.Trace( "Loading files...");
            Log.PushIndent();
            FileManager.LoadFiles();
            Log.PopIndent();

            uint[] hues = FileManager.Hues.CreateShaderColors();

            int size = FileManager.Hues.HuesCount;

            Texture2D texture0 = new Texture2D(GraphicsDevice, 32, size * 2);
            texture0.SetData(hues, 0, size * 2);
            Texture2D texture1 = new Texture2D(GraphicsDevice, 32, size);
            texture1.SetData(hues, size, size);
            GraphicsDevice.Textures[1] = texture0;
            GraphicsDevice.Textures[2] = texture1;

            AuraManager.CreateAuraTexture();

            Log.Trace( "Network calibration...");
            Log.PushIndent();
            PacketHandlers.Load();
            //ATTENTION: you will need to enable ALSO ultimalive server-side, or this code will have absolutely no effect!
            UltimaLive.Enable();
            PacketsTable.AdjustPacketSizeByVersion(FileManager.ClientVersion);
            Log.Trace( "Done!");
            Log.PopIndent();

            Log.Trace( "Loading plugins...");
            Log.PushIndent();

            UIManager.InitializeGameCursor();

            foreach (var p in Settings.GlobalSettings.Plugins)
                Plugin.Create(p);
            Log.Trace( "Done!");
            Log.PopIndent();


            UoAssist.Start();
        }


        private float _totalElapsed, _currentFpsTime;
        private uint _totalFrames;

        protected override void Update(GameTime gameTime)
        {
            if (Profiler.InContext("OutOfContext"))
                Profiler.ExitContext("OutOfContext");

            Time.Ticks = (uint) gameTime.TotalGameTime.TotalMilliseconds;

            Mouse.Update();
            OnNetworkUpdate(gameTime.TotalGameTime.TotalMilliseconds, gameTime.ElapsedGameTime.TotalMilliseconds);
            UIManager.Update(gameTime.TotalGameTime.TotalMilliseconds, gameTime.ElapsedGameTime.TotalMilliseconds);
            Plugin.Tick();

            if (_scene != null && _scene.IsLoaded && !_scene.IsDestroyed)
            {
                Profiler.EnterContext("Update");
                _scene.Update(gameTime.TotalGameTime.TotalMilliseconds, gameTime.ElapsedGameTime.TotalMilliseconds);
                Profiler.ExitContext("Update");
            }

            _totalElapsed += (float) gameTime.ElapsedGameTime.TotalMilliseconds;
            _currentFpsTime += (float) gameTime.ElapsedGameTime.TotalMilliseconds;

            if (_currentFpsTime >= 1000)
            {
                CUOEnviroment.CurrentRefreshRate = _totalFrames;

                _totalFrames = 0;
                _currentFpsTime = 0;
            }

            float x = _intervalFixedUpdate[!IsActive && ProfileManager.Current != null && ProfileManager.Current.ReduceFPSWhenInactive ? 1 : 0];

            if (_totalElapsed > x)
            {
                if (_scene != null && _scene.IsLoaded && !_scene.IsDestroyed)
                {
                    Profiler.EnterContext("FixedUpdate");
                    _scene.FixedUpdate(gameTime.TotalGameTime.TotalMilliseconds, gameTime.ElapsedGameTime.TotalMilliseconds);
                    Profiler.ExitContext("FixedUpdate");
                }

                _totalElapsed %= x;
            }
            else
            {
                SuppressDraw();

                if (!gameTime.IsRunningSlowly)
                {
                    Thread.Sleep(1);
                }
            }

            base.Update(gameTime);
        }

        private RenderTarget2D _buffer;

        protected override void Draw(GameTime gameTime)
        {
            Profiler.EndFrame();
            Profiler.BeginFrame();

            if (Profiler.InContext("OutOfContext"))
                Profiler.ExitContext("OutOfContext");
            Profiler.EnterContext("RenderFrame");

            _totalFrames++;

            if (_scene != null && _scene.IsLoaded && !_scene.IsDestroyed)
                _scene.Draw(_uoSpriteBatch);

            GraphicsDevice.SetRenderTarget(_buffer);
            UIManager.Draw(_uoSpriteBatch);

            if (ProfileManager.Current != null && ProfileManager.Current.ShowNetworkStats)
            {
                if (!NetClient.Socket.IsConnected)
                    NetClient.LoginSocket.Statistics.Draw(_uoSpriteBatch, 10, 50);
                else if (!NetClient.Socket.IsDisposed)
                    NetClient.Socket.Statistics.Draw(_uoSpriteBatch, 10, 50);
            }


            base.Draw(gameTime);

            Profiler.ExitContext("RenderFrame");
            Profiler.EnterContext("OutOfContext");

            GraphicsDevice.SetRenderTarget(null);

            _imGuiRenderer.BeforeLayout((float) gameTime.ElapsedGameTime.TotalSeconds);
            DrawLayout();
            _imGuiRenderer.AfterLayout();

            UpdateWindowCaption(gameTime);
        }

        private byte[] _textBuffer = new byte[256];

        private void DrawLayout()
        {
            ImGui.SetNextWindowPos(System.Numerics.Vector2.Zero);
            ImGui.SetNextWindowSize(new System.Numerics.Vector2(_graphicDeviceManager.PreferredBackBufferWidth, 
                                                                _graphicDeviceManager.PreferredBackBufferHeight));
            ImGuiStylePtr stylePtr = ImGui.GetStyle();

            float prevWinBorderSize = stylePtr.WindowBorderSize;
            var prevWinPadding = stylePtr.WindowPadding;


            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, System.Numerics.Vector2.Zero);

            ImGui.Begin("MainWindow",
                        ImGuiWindowFlags.NoMove | 
                        ImGuiWindowFlags.NoResize | 
                        ImGuiWindowFlags.NoTitleBar | 
                        ImGuiWindowFlags.NoScrollbar |
                        ImGuiWindowFlags.NoSavedSettings | 
                        ImGuiWindowFlags.NoCollapse | 
                        ImGuiWindowFlags.NoBringToFrontOnFocus | 
                        ImGuiWindowFlags.NoInputs);

            ImGui.Image(_bufferPtr, new System.Numerics.Vector2(_graphicDeviceManager.PreferredBackBufferWidth, _graphicDeviceManager.PreferredBackBufferHeight));

            // child
            {
                ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, prevWinBorderSize);
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, prevWinPadding);

               
                ImGui.SetNextWindowSize(new System.Numerics.Vector2(_graphicDeviceManager.PreferredBackBufferWidth / 2,
                                                                    _graphicDeviceManager.PreferredBackBufferHeight / 2), ImGuiCond.FirstUseEver);

                ImGui.Begin("Child window##");

                ImGui.InputText("XX", _textBuffer, (uint) _textBuffer.Length);

                ImGui.Text("FPS: " + ImGui.GetIO().Framerate);
               
                ImGui.End();

                DrawLauncher();

                ImGui.PopStyleVar();
                ImGui.PopStyleVar();
            }

            stylePtr.WindowBorderSize = 0;
            stylePtr.WindowPadding = Vector2.Zero;

            ImGui.End();

            ImGui.PopStyleVar();
            ImGui.PopStyleVar();
        }


        private int _currentItemListBoxProfile;
        private string[] _listBoxItems = Enumerable.Repeat("", 100).ToArray();
        private byte[] _profileName = new byte[32], _username = new byte[32], _password = new byte[32], _uoPath = new byte[256];
        private bool _cryptPassword, _saveCredentials;

        private void DrawLauncher()
        {
            ImGui.SetNextWindowPos(System.Numerics.Vector2.Zero);
            ImGui.SetNextWindowSize(new System.Numerics.Vector2(_graphicDeviceManager.PreferredBackBufferWidth,
                                                                _graphicDeviceManager.PreferredBackBufferHeight));

            //ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
            //ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, System.Numerics.Vector2.Zero);

            ImGui.Begin("ClassicUO Configuration",
                        ImGuiWindowFlags.NoMove |
                        ImGuiWindowFlags.NoResize |
                        ImGuiWindowFlags.NoTitleBar |
                        ImGuiWindowFlags.NoScrollbar |
                        ImGuiWindowFlags.NoSavedSettings |
                        ImGuiWindowFlags.NoCollapse);


            {
                //ImGui.ListBoxHeader("Profile");
                ImGui.PushItemWidth(100);
                ImGui.ListBox("", ref _currentItemListBoxProfile, _listBoxItems, 100);
                ImGui.PopItemWidth();

                if (ImGui.CollapsingHeader("Profile Configuration"))
                {
                    ImGui.InputText("Profile name", _profileName, (uint) _profileName.Length);
                    ImGui.Dummy(new Vector2(0, 20));
                    ImGui.InputText("Username", _username, (uint) _username.Length);
                    ImGui.InputText("Password", _password, (uint) _password.Length);
                    ImGui.SameLine();
                    ImGui.Checkbox("Crypt password", ref _cryptPassword);
                    ImGui.Checkbox("Save credentials", ref _saveCredentials);
                    ImGui.InputText("UO Path", _uoPath, (uint) _uoPath.Length);
                    ImGui.SameLine();
                    ImGui.SmallButton("...");
                }



            }


            ImGui.End();


            //ImGui.PopStyleVar();
            //ImGui.PopStyleVar();
        }

        private void UpdateWindowCaption(GameTime gameTime)
        {
            if (!Settings.GlobalSettings.Profiler || CUOEnviroment.DisableUpdateWindowCaption)
                return;

            double timeDraw = Profiler.GetContext("RenderFrame").TimeInContext;
            double timeUpdate = Profiler.GetContext("Update").TimeInContext;
            double timeFixedUpdate = Profiler.GetContext("FixedUpdate").TimeInContext;
            double timeOutOfContext = Profiler.GetContext("OutOfContext").TimeInContext;
            //double timeTotalCheck = timeOutOfContext + timeDraw + timeUpdate;
            double timeTotal = Profiler.TrackedTime;
            double avgDrawMs = Profiler.GetContext("RenderFrame").AverageTime;

#if DEV_BUILD
            Window.Title = string.Format("ClassicUO [dev] {5} - Draw:{0:0.0}% Update:{1:0.0}% FixedUpd:{6:0.0} AvgDraw:{2:0.0}ms {3} - FPS: {4}", 100d * (timeDraw / timeTotal), 100d * (timeUpdate / timeTotal), avgDrawMs, gameTime.IsRunningSlowly ? "*" : string.Empty, CUOEnviroment.CurrentRefreshRate, CUOEnviroment.Version, 100d * (timeFixedUpdate / timeTotal));
#else
            Window.Title = string.Format("ClassicUO {5} - Draw:{0:0.0}% Update:{1:0.0}% FixedUpd:{6:0.0} AvgDraw:{2:0.0}ms {3} - FPS: {4}", 100d * (timeDraw / timeTotal), 100d * (timeUpdate / timeTotal), avgDrawMs, gameTime.IsRunningSlowly ? "*" : string.Empty, CUOEnviroment.CurrentRefreshRate, CUOEnviroment.Version, 100d * (timeFixedUpdate / timeTotal));
#endif
        }

        private void OnNetworkUpdate(double totalMS, double frameMS)
        {
            if (NetClient.LoginSocket.IsDisposed && NetClient.LoginSocket.IsConnected)
                NetClient.LoginSocket.Disconnect();
            else if (!NetClient.Socket.IsConnected)
            {
                NetClient.LoginSocket.Update();
                UpdateSockeStats(NetClient.LoginSocket, totalMS);
            }
            else if (!NetClient.Socket.IsDisposed)
            {
                NetClient.Socket.Update();
                UpdateSockeStats(NetClient.Socket, totalMS);
            }
        }

        private void UpdateSockeStats(NetClient socket, double totalMS)
        {
            if (_statisticsTimer < totalMS)
            {
                socket.Statistics.Update();
                _statisticsTimer = totalMS + 500;
            }
        }

        public override void OnSDLEvent(ref SDL_Event ev)
        {
            HandleSDLEvent(ref ev);
            base.OnSDLEvent(ref ev);
        }

        private void WindowOnClientSizeChanged(object sender, EventArgs e)
        {
            int width = Window.ClientBounds.Width;
            int height = Window.ClientBounds.Height;

            if (CUOEnviroment.IsHighDPI)
            {
                //TODO:
            }

            uint flags = SDL.SDL_GetWindowFlags(Window.Handle);
            if ((flags & (uint) SDL.SDL_WindowFlags.SDL_WINDOW_MAXIMIZED) == 0)
            {
                // TODO: option set WindowClientBounds
                ProfileManager.Current.WindowClientBounds = new Point(width, height);
            }

            SetWindowSize(width, height);

            var viewport = UIManager.GetGump<WorldViewportGump>();

            if (viewport != null && ProfileManager.Current.GameWindowFullSize)
            {
                viewport.ResizeGameWindow(new Point(width, height));
                viewport.X = -5;
                viewport.Y = -5;
            }

            if (ProfileManager.Current.WindowBorderless) SetWindowBorderless(true);
        }


        private unsafe void HandleSDLEvent(ref SDL.SDL_Event e)
        {
            switch (e.type)
            {
                case SDL.SDL_EventType.SDL_AUDIODEVICEADDED:
                    Console.WriteLine("AUDIO ADDED: {0}", e.adevice.which);

                    break;

                case SDL.SDL_EventType.SDL_AUDIODEVICEREMOVED:
                    Console.WriteLine("AUDIO REMOVED: {0}", e.adevice.which);

                    break;


                case SDL.SDL_EventType.SDL_WINDOWEVENT:

                    switch (e.window.windowEvent)
                    {
                        case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_ENTER:
                            Mouse.MouseInWindow = true;

                            break;

                        case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_LEAVE:
                            Mouse.MouseInWindow = false;

                            break;

                        case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_FOCUS_GAINED:
                            Plugin.OnFocusGained();

                            // SDL_CaptureMouse(SDL_bool.SDL_TRUE);
                            //Log.Debug("FOCUS");
                            break;

                        case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_FOCUS_LOST:
                            Plugin.OnFocusLost();
                            //Log.Debug("NO FOCUS");
                            //SDL_CaptureMouse(SDL_bool.SDL_FALSE);

                            break;

                        case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_TAKE_FOCUS:

                            //Log.Debug("TAKE FOCUS");
                            break;

                        case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_HIT_TEST:

                            break;
                    }

                    break;

                case SDL.SDL_EventType.SDL_SYSWMEVENT:

                    break;

                case SDL.SDL_EventType.SDL_KEYDOWN:
                    var io = ImGui.GetIO();
                    //io.KeyShift = e.key.keysym.mod == SDL_Keymod.KMOD_SHIFT;
                    //io.KeyCtrl = e.key.keysym.mod == SDL_Keymod.KMOD_CTRL;
                    //io.KeyAlt = e.key.keysym.mod == SDL_Keymod.KMOD_ALT;
                    //io.KeySuper = e.key.keymod == super

                    if (io.WantCaptureMouse)
                    {
                        return;
                    }

                    Keyboard.OnKeyDown(e.key);

                    if (Plugin.ProcessHotkeys((int) e.key.keysym.sym, (int) e.key.keysym.mod, true))
                    {
                        _ignoreNextTextInput = false;

                        //UIManager.MouseOverControl?.InvokeKeyDown(e.key.keysym.sym, e.key.keysym.mod);
                        //if (UIManager.MouseOverControl != UIManager.KeyboardFocusControl)
                        UIManager.KeyboardFocusControl?.InvokeKeyDown(e.key.keysym.sym, e.key.keysym.mod);

                        _scene.OnKeyDown(e.key);
                    }
                    else
                        _ignoreNextTextInput = true;

                    break;

                case SDL.SDL_EventType.SDL_KEYUP:
                    io = ImGui.GetIO();

                    if (io.WantCaptureMouse)
                    {
                        return;
                    }

                    Keyboard.OnKeyUp(e.key);

                    //UIManager.MouseOverControl?.InvokeKeyUp(e.key.keysym.sym, e.key.keysym.mod);
                    //if (UIManager.MouseOverControl != UIManager.KeyboardFocusControl)
                    UIManager.KeyboardFocusControl?.InvokeKeyUp(e.key.keysym.sym, e.key.keysym.mod);

                    _scene.OnKeyUp(e.key);

                    break;

                case SDL.SDL_EventType.SDL_TEXTINPUT:
                    io = ImGui.GetIO();

                    if (io.WantCaptureMouse)
                    {
                        return;
                    }

                    if (_ignoreNextTextInput)
                        break;

                    fixed (SDL.SDL_Event* ev = &e)
                    {
                        string s = StringHelper.ReadUTF8(ev->text.text);

                        if (!string.IsNullOrEmpty(s))
                        {
                            UIManager.KeyboardFocusControl?.InvokeTextInput(s);
                            _scene.OnTextInput(s);
                        }
                    }

                    break;

                case SDL.SDL_EventType.SDL_MOUSEMOTION:
                    Mouse.Update();

                    io = ImGui.GetIO();
                    //io.MousePos = new Vector2(Mouse.Position.X, Mouse.Position.Y);

                    if (io.WantCaptureMouse)
                    {
                        return;
                    }

                    if (Mouse.IsDragging)
                    {
                        UIManager.OnMouseDragging();
                        _scene.OnMouseDragging();
                    }

                    if (Mouse.IsDragging && !_dragStarted)
                    {
                        _dragStarted = true;
                    }

                    break;

                case SDL.SDL_EventType.SDL_MOUSEWHEEL:
                    Mouse.Update();
                    bool isup = e.wheel.y > 0;
                    io = ImGui.GetIO();
                    //io.MouseWheel = e.wheel.y;

                    if (io.WantCaptureMouse)
                    {
                        return;
                    }

                    Plugin.ProcessMouse(0, e.wheel.y);

                    UIManager.OnMouseWheel(isup);
                    _scene.OnMouseWheel(isup);

                    break;

                case SDL.SDL_EventType.SDL_MOUSEBUTTONUP:
                case SDL.SDL_EventType.SDL_MOUSEBUTTONDOWN:
                    Mouse.Update();
                    bool isDown = e.type == SDL.SDL_EventType.SDL_MOUSEBUTTONDOWN;
                    bool resetTime = false;
                    io = ImGui.GetIO();

                    if (_dragStarted && !isDown)
                    {
                        _dragStarted = false;
                        resetTime = true;
                    }

                    SDL.SDL_MouseButtonEvent mouse = e.button;

                    switch ((uint) mouse.button)
                    {
                        case SDL_BUTTON_LEFT:

                            if (isDown)
                            {
                                //io.MouseDown[0] = true;

                                Mouse.Begin();
                                Mouse.LButtonPressed = true;
                                Mouse.LDropPosition = Mouse.Position;
                                Mouse.CancelDoubleClick = false;
                                uint ticks = SDL_GetTicks();


                                if (!io.WantCaptureMouse)
                                {
                                    if (Mouse.LastLeftButtonClickTime + Mouse.MOUSE_DELAY_DOUBLE_CLICK >= ticks)
                                    {
                                        Mouse.LastLeftButtonClickTime = 0;
                                        bool res;

                                        if (UIManager.ValidForDClick())
                                        {
                                            res = UIManager.OnLeftMouseDoubleClick();
                                        }
                                        else
                                            res = _scene.OnLeftMouseDoubleClick();

                                        //bool res = _scene.OnLeftMouseDoubleClick() || UIManager.OnLeftMouseDoubleClick();

                                        MouseDoubleClickEventArgs arg = new MouseDoubleClickEventArgs(Mouse.Position.X, Mouse.Position.Y, MouseButton.Left);

                                        if (!arg.Result && !res)
                                        {
                                            _scene.OnLeftMouseDown();
                                            UIManager.OnLeftMouseButtonDown();
                                        }
                                        else
                                            Mouse.LastLeftButtonClickTime = 0xFFFF_FFFF;

                                        break;
                                    }

                                    _scene.OnLeftMouseDown();
                                    UIManager.OnLeftMouseButtonDown();
                                }

                                Mouse.LastLeftButtonClickTime = Mouse.CancelDoubleClick ? 0 : ticks;
                            }
                            else
                            {
                                //io.MouseDown[0] = false;

                                if (resetTime)
                                    Mouse.LastLeftButtonClickTime = 0;

                                if (!io.WantCaptureMouse)
                                {
                                    if (Mouse.LastLeftButtonClickTime != 0xFFFF_FFFF)
                                    {
                                        _scene.OnLeftMouseUp();
                                        UIManager.OnLeftMouseButtonUp();
                                    }
                                }

                                Mouse.LButtonPressed = false;
                                Mouse.End();

                                Mouse.LastClickPosition = Mouse.Position;
                            }

                            break;

                        case SDL_BUTTON_MIDDLE:

                            if (isDown)
                            {
                                //io.MouseDown[1] = true;

                                Mouse.Begin();
                                Mouse.MButtonPressed = true;
                                Mouse.MDropPosition = Mouse.Position;
                                Mouse.CancelDoubleClick = false;
                                uint ticks = SDL_GetTicks();

                                if (!io.WantCaptureMouse)
                                {
                                    if (Mouse.LastMidButtonClickTime + Mouse.MOUSE_DELAY_DOUBLE_CLICK >= ticks)
                                    {
                                        Mouse.LastMidButtonClickTime = 0;
                                        var res = _scene.OnMiddleMouseDoubleClick();

                                        MouseDoubleClickEventArgs arg = new MouseDoubleClickEventArgs(Mouse.Position.X, Mouse.Position.Y, MouseButton.Middle);

                                        if (!arg.Result && !res)
                                        {
                                            _scene.OnMiddleMouseDown();
                                        }

                                        break;
                                    }

                                    Plugin.ProcessMouse(e.button.button, 0);

                                    _scene.OnMiddleMouseDown();
                                }

                                Mouse.LastMidButtonClickTime = Mouse.CancelDoubleClick ? 0 : ticks;
                            }
                            else
                            {
                                //io.MouseDown[1] = false;
                                Mouse.MButtonPressed = false;
                                Mouse.End();
                            }

                            break;

                        case SDL_BUTTON_RIGHT:

                            if (isDown)
                            {
                                //io.MouseDown[2] = true;
                               
                                Mouse.Begin();
                                Mouse.RButtonPressed = true;
                                Mouse.RDropPosition = Mouse.Position;
                                Mouse.CancelDoubleClick = false;
                                uint ticks = SDL_GetTicks();

                                if (!io.WantCaptureMouse)
                                {
                                    if (Mouse.LastRightButtonClickTime + Mouse.MOUSE_DELAY_DOUBLE_CLICK >= ticks)
                                    {
                                        Mouse.LastRightButtonClickTime = 0;

                                        var res = _scene.OnRightMouseDoubleClick() || UIManager.OnRightMouseDoubleClick();

                                        MouseDoubleClickEventArgs arg = new MouseDoubleClickEventArgs(Mouse.Position.X, Mouse.Position.Y, MouseButton.Right);

                                        if (!arg.Result && !res)
                                        {
                                            _scene.OnRightMouseDown();
                                            UIManager.OnRightMouseButtonDown();
                                        }
                                        else
                                            Mouse.LastRightButtonClickTime = 0xFFFF_FFFF;

                                        break;
                                    }

                                    _scene.OnRightMouseDown();
                                    UIManager.OnRightMouseButtonDown();
                                }

                                Mouse.LastRightButtonClickTime = Mouse.CancelDoubleClick ? 0 : ticks;
                            }
                            else
                            {
                                //io.MouseDown[2] = false;

                                if (resetTime)
                                    Mouse.LastRightButtonClickTime = 0;

                                if (!io.WantCaptureMouse)
                                {
                                    if (Mouse.LastRightButtonClickTime != 0xFFFF_FFFF)
                                    {
                                        _scene.OnRightMouseUp();
                                        UIManager.OnRightMouseButtonUp();
                                    }
                                }

                                Mouse.RButtonPressed = false;
                                Mouse.End();
                            }

                            break;

                        case SDL_BUTTON_X1:

                            if (isDown)
                                Plugin.ProcessMouse(e.button.button, 0);

                            break;

                        case SDL_BUTTON_X2:

                            if (isDown)
                                Plugin.ProcessMouse(e.button.button, 0);

                            break;
                    }

                    break;
            }
        }
    }
}
