﻿using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ComponentModel.Design;
using System.Text;
using System.Threading;
using System.Windows;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell;
using EnvDTE;
using EnvDTE80;
using Thread = System.Threading.Thread;

namespace Trik.Upload_Extension
{
    /// <summary>
    /// This is the class that implements the package exposed by this assembly.
    ///
    /// The minimum requirement for a class to be considered a valid package for Visual Studio
    /// is to implement the IVsPackage interface and register itself with the shell.
    /// This package uses the helper classes defined inside the Managed Package Framework (MPF)
    /// to do it: it derives from the Package class that provides the implementation of the 
    /// IVsPackage interface and uses the registration attributes defined in the framework to 
    /// register itself and its components with the shell.
    /// </summary>
    // This attribute tells the PkgDef creation utility (CreatePkgDef.exe) that this class is
    // a package.
    [PackageRegistration(UseManagedResourcesOnly = true)]
    // This attribute is used to register the information needed to show this package
    // in the Help/About dialog of Visual Studio.
    [InstalledProductRegistration("#110", "#112", "1.0", IconResourceID = 400)]
    // This attribute is needed to let the shell know that this package exposes some menus.
    [ProvideMenuResource("Menus.ctmenu", 1)]
    // This attribute registers a tool window exposed by this package.
    //[ProvideToolWindow(typeof(MyToolWindow))]
    [Guid(GuidList.guidUpload_ExtensionPkgString)]
    public sealed class UploadExtensionPackage : Package
    {
        private Uploader uploader;
        private Window1 connectionWindow;
#if DEBUG 
        private string ip = "10.0.40.161";
#else   
        private string ip = "192.168.1.1";
#endif
        private bool firstUpload = true;

        //Visual Studio communication constants 
        private bool _isProgressRunning;
        private uint _statusbarCookie;
        private IVsStatusbar _statusbar;
        private IVsOutputWindowPane _pane;
        private bool _isTRIKAplicationRunning;
        private bool _isFirstRun = true;

        /// <summary>
        /// Default constructor of the package.
        /// Inside this method you can place any initialization code that does not require 
        /// any Visual Studio service because at this point the package object is created but 
        /// not sited yet inside Visual Studio environment. The place to do all the other 
        /// initialization is the Initialize method.
        /// </summary>
        public UploadExtensionPackage()
        {
            Debug.WriteLine(string.Format(CultureInfo.CurrentCulture, "Entering constructor for: {0}", ToString()));
        }

        /// <summary>
        /// This function is called when the user clicks the menu item that shows the 
        /// tool window. See the Initialize method to see how the menu item is associated to 
        /// this function using the OleMenuCommandService service and the MenuCommand class.
        /// </summary>
        private void ShowToolWindow(object sender, EventArgs e)
        {
            // Get the instance number 0 of this tool window. This window is single instance so this instance
            // is actually the only one.
            // The last flag is set to true so that if the tool window does not exists it will be created.
            var window = FindToolWindow(typeof(MyToolWindow), 0, true);
            if ((null == window) || (null == window.Frame))
            {
                throw new NotSupportedException(Resources.CanNotCreateWindow);
            }
            var windowFrame = (IVsWindowFrame)window.Frame;
            ErrorHandler.ThrowOnFailure(windowFrame.Show());
        }


        /////////////////////////////////////////////////////////////////////////////
        // Overridden Package Implementation
        #region Package Members

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the initialization code that rely on services provided by VisualStudio.
        /// </summary>
        protected override void Initialize()
        {
            var dte = (DTE2)GetService(typeof(DTE));
            
            Debug.WriteLine (string.Format(CultureInfo.CurrentCulture, "Entering Initialize() of: {0}", ToString()));
            base.Initialize();
            
            // Add our command handlers for menu (commands must exist in the .vsct file)
            var mcs = GetService(typeof(IMenuCommandService)) as OleMenuCommandService;
            if ( null != mcs )
            {
                // Create the command for the menu item.
                var menuCommandID = new CommandID(GuidList.guidUpload_ExtensionCmdSet, (int)PkgCmdIDList.uploadToTRIK);
                var menuItem = new MenuCommand(MenuItemCallback, menuCommandID );
                mcs.AddCommand( menuItem );
                // Create the command for the tool window
                var toolwndCommandID = new CommandID(GuidList.guidUpload_ExtensionCmdSet, (int)PkgCmdIDList.uploadTRIKWindow);
                var menuToolWin = new MenuCommand(ShowToolWindow, toolwndCommandID);
                mcs.AddCommand( menuToolWin );
            }
        }
        #endregion

        /// <summary>
        /// This function is the callback used to execute a command when the a menu item is clicked.
        /// See the Initialize method to see how the menu item is associated to this function using
        /// the OleMenuCommandService service and the MenuCommand class.
        /// </summary>
        private void MenuItemCallback(object sender, EventArgs e)
        {
            connectionWindow = new Window1 {IpAddress = {Text = ip}};
            if (null == uploader)
            {
                connectionWindow.UploadToTrik.IsEnabled = false;
                connectionWindow.RunProgram.IsEnabled = false;
            }

            connectionWindow.ConnectToTrik.Click += ConnectToTrik_Click;
            connectionWindow.UploadToTrik.Click += UploadToTrik_Click;
            connectionWindow.RunProgram.Click += RunProgram_Click;
            WindowPane.SetName("TRIK-Controller");
            connectionWindow.ShowModal();

        }

        private void RunProgram_Click(object sender, RoutedEventArgs e)
        {
            if (_isTRIKAplicationRunning) return;
            _isTRIKAplicationRunning = true;
            //ReportProgress(10000, "Starting an application on a controller");
            var scnt = SynchronizationContext.Current;
            connectionWindow.Close();
            
            //WindowPane.Hide();
            WindowPane.Clear();
            WindowPane.Activate();
            WindowPane.FlushToTaskList();
            WindowPane.OutputString("========== Starting an Application on TRIK ==========\n");

            System.Threading.Tasks.Task.Run(() =>
            {
                StopProgress();
                StatusBar.SetText("Running application on TRIK. See output pane for more information");
                try
                {
                    var programOutput = uploader.RunProgram();
                    
                    if (!_isFirstRun) return;
                    
                    programOutput.DataReceived += programOutput_DataReceived;
                    _isFirstRun = false;

                    //WindowPane.OutputStringThreadSafe(programOutput + "\n");
                    }
                catch (Exception exception)
                {
                    scnt.Post(x =>
                    {
                        connectionWindow.MessageLabel.Content =
                            "Network error occurred while running an application. Trying to reconnect";
                        connectionWindow.RunProgram.IsEnabled = false;
                        connectionWindow.UploadToTrik.IsEnabled = false;
                    }, null);
                    WindowPane.OutputString(exception.Message);

                    Reconnect(scnt);
                }
                finally
                {
                    _isTRIKAplicationRunning = false;
                }
            });
        }

        void programOutput_DataReceived(object sender, Renci.SshNet.Common.ShellDataEventArgs e)
        {
            WindowPane.OutputStringThreadSafe(Encoding.UTF8.GetString(e.Data));
        }

        void UploadToTrik_Click(object sender, RoutedEventArgs e)
        {
            if (uploader == null) return;

            connectionWindow.MessageLabel.Content = "Uploading...";
            StatusBar.SetText("Uploading...");
            connectionWindow.UploadToTrik.IsEnabled = false;
            connectionWindow.RunProgram.IsEnabled = false;
            var dte = (DTE2)GetService(typeof(DTE));
            var buildConfiguration = dte.Solution.SolutionBuild.ActiveConfiguration.Name;

            if ("Release" != buildConfiguration)
            {
                const string message = "Use Release build for better performance";
                connectionWindow.MessageLabel.Content = message;
                StatusBar.SetText(message);
                connectionWindow.UploadToTrik.IsEnabled = true;
                return;
            }

            var scnt = SynchronizationContext.Current;
            System.Threading.Tasks.Task.Run(() =>
            {
                var projects = dte.Solution.Projects;
                if (projects.Count > 2)
                {
                    const string message =
                        "Your solution has several projects. Working with several projects is not supported!";
                    scnt.Post(x => connectionWindow.MessageLabel.Content = message, null);
                    StatusBar.SetText(message);
                    return;
                }
                Project project;
                try
                {
                    project = projects.Cast<Project>().First();
                }
                catch (Exception)
                {
                    const string message = "Possibly there's no project";
                    scnt.Post(x => connectionWindow.MessageLabel.Content = message, null);
                    StatusBar.SetText(message);
                    return;
                }

                if (!Directory.Exists(Path.GetDirectoryName(project.FullName) + @"\bin\Release"))
                {
                    const string message = "Build the project before uploading";
                    scnt.Post(x => connectionWindow.MessageLabel.Content = message, null);
                    StatusBar.SetText(message);
                    return;
                }

                
                try
                { 
                    uploader.ProjectPath = project.FullName;
                    ReportProgress(8000, "Uploading");
                    uploader.Update();
                    scnt.Post(x =>
                    {
                        connectionWindow.MessageLabel.Content = "Uploaded!";
                        //connectionWindow.UploadToTrik.IsEnabled = true;
                        connectionWindow.Close();
                    }, null);
                    StopProgress();
                    StatusBar.SetText("Uploaded!");
                }
                catch (Exception)
                {
                    StopProgress();
                    scnt.Post(x =>
                    {
                        connectionWindow.MessageLabel.Content = "Error is occurred. Trying to reconnect...";
                    }, null);
                    //StatusBar.SetText("Error is occurred. Trying to reconnect...");
                    Reconnect(scnt);

                }                
            });
        }

        void ConnectToTrik_Click(object sender, RoutedEventArgs e)
        {
            if (ip == connectionWindow.IpAddress.Text && !firstUpload)
            {
                connectionWindow.MessageLabel.Content = "Already connected to this host!";
                StatusBar.SetText("Already connected to this host!");
                return;
            }
            connectionWindow.ConnectToTrik.IsEnabled = false;
            connectionWindow.RunProgram.IsEnabled = false;

            connectionWindow.MessageLabel.Content = "Connecting...";
            ip = connectionWindow.IpAddress.Text;
            var scnt = SynchronizationContext.Current;
            connectionWindow.UploadToTrik.IsEnabled = false;
            System.Threading.Tasks.Task.Run(() =>
            {
                const int dueTime = 11000; //Usual time is taken for connection with a controller
                const string message = "A connection is taking longer than usual";
                var timeoutTimer = new Timer(x =>
                {
                    StatusBar.SetText(message);
                    scnt.Post(y =>
                    {
                        connectionWindow.MessageLabel.Content = message;
                    }, null);
                }, null, dueTime, -1);

                ReportProgress(dueTime, "Connecting");

                try
                {
                    uploader = new Uploader(ip);
                    scnt.Post(x =>
                    {
                        connectionWindow.MessageLabel.Content = "Connected!";
                        connectionWindow.UploadToTrik.IsEnabled = true;
                        firstUpload = false;

                    }
                    , null);
                    StopProgress();
                    StatusBar.SetText("Connected!");
                }
                catch (Exception exeption)
                {
                    StopProgress();
                    StatusBar.SetText("Connection attempt failed. See Output pane for details");
                    WindowPane.Clear();
                    WindowPane.Activate();
                    WindowPane.OutputString(exeption.Message);
                    scnt.Post(x => connectionWindow.MessageLabel.Content = "Connection attempt failed", null);
                }
                finally
                {
                    scnt.Post(x => connectionWindow.ConnectToTrik.IsEnabled = true , null);
                    timeoutTimer.Dispose();
                }
            });
        }

        private void ReportProgress(int period, String message)
        {
            
            StopProgress();
            _isProgressRunning = true;
            
            System.Threading.Tasks.Task.Run(() =>
            {
                StatusBar.SetText("");
                var messageTail = "";
                const int iterations = 10;
                while (_isProgressRunning)
                {
                    for (var i = (uint) 0; i < iterations; i++)
                    {
                        StatusBar.Progress(ref _statusbarCookie, _isProgressRunning?1:0, message + messageTail, i, iterations);
                        messageTail = "." + ((messageTail.Length < 3) ? messageTail : "");
                        Thread.Sleep(period/iterations);
                    }
                }
            });
        }
        private void Reconnect(SynchronizationContext scnt)
        {
            try
            {
                ReportProgress(8000, "Network error is occurred. Trying to reconnect");
                uploader.Reconnect();
                StopProgress();
                StatusBar.SetText("Connected!");
                scnt.Post(x =>
                {
                    connectionWindow.MessageLabel.Content = "Connected!";
                    connectionWindow.UploadToTrik.IsEnabled = true;
                    firstUpload = false;
                }
                    , null);
            }
            catch (Exception)
            {
                StopProgress();
                const string message = "Can't connect to TRIK. Check connection and try again";
                StatusBar.SetText(message);
                scnt.Post(x =>
                {
                    connectionWindow.MessageLabel.Content = message;
                    connectionWindow.UploadToTrik.IsEnabled = true;
                    firstUpload = false;
                }
                    , null);
                uploader = null;
                firstUpload = true;
            }
        }

        private void StopProgress()
        {
            _isProgressRunning = false;
            StatusBar.Progress(ref _statusbarCookie, 0, "", 0, 0);
        }

        private IVsStatusbar StatusBar
        {
            get { return _statusbar ?? (_statusbar = GetService(typeof (SVsStatusbar)) as IVsStatusbar); }
        }
        private IVsOutputWindowPane WindowPane
        {
            get { return _pane ?? (_pane = GetService(typeof (SVsGeneralOutputWindowPane)) as IVsOutputWindowPane); }
        }
    }
}
