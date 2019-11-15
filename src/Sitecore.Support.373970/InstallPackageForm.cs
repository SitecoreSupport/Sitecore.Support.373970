namespace Sitecore.Support.Shell.Applications.Install.Dialogs.InstallPackage
{
    using Sitecore;
    using Sitecore.Configuration;
    using Sitecore.Data.Engines;
    using Sitecore.Diagnostics;
    using Sitecore.Globalization;
    using Sitecore.Install.Files;
    using Sitecore.Install.Framework;
    using Sitecore.Install.Items;
    using Sitecore.Install.Security;
    using Sitecore.Install.Utils;
    using Sitecore.IO;
    using Sitecore.Jobs.AsyncUI;
    using Sitecore.SecurityModel;
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Threading;
    using Sitecore.Web.UI.HtmlControls;
    using Sitecore.Web.UI.Pages;
    using Sitecore.Web.UI.Sheer;
    using Sitecore.Shell.Framework;
    using Sitecore.Install.Metadata;
    using Sitecore.Install.Events;
    using Sitecore.Data;
    using Sitecore.Install.Zip;
    using Sitecore.Events;
    using System.IO;
    using Sitecore.Jobs;
    using Sitecore.Web;
    using Sitecore.Support.Install;

    public class InstallPackageForm : WizardForm
    {
        protected Edit PackageFile;
        protected Edit PackageName;
        protected Edit Version;
        protected Edit Author;
        protected Edit Publisher;
        protected Border LicenseAgreement;
        protected Memo ReadmeText;
        protected Radiobutton Decline;
        protected Radiobutton Accept;
        protected Checkbox Restart;
        protected Checkbox RestartServer;
        protected JobMonitor Monitor;
        protected Literal FailingReason;
        protected Literal ErrorDescription;
        protected Border SuccessMessage;
        protected Border ErrorMessage;
        protected Border AbortMessage;
        private readonly object CurrentStepSync = new object();

        protected override void ActivePageChanged(string page, string oldPage)
        {
            base.ActivePageChanged(page, oldPage);
            base.NextButton.Header = this.OriginalNextButtonHeader;
            if ((page == "License") && (oldPage == "LoadPackage"))
            {
                base.NextButton.Disabled = !this.Accept.Checked;
            }
            if (page == "Installing")
            {
                base.BackButton.Disabled = true;
                base.NextButton.Disabled = true;
                base.CancelButton.Disabled = true;
                Context.ClientPage.SendMessage(this, "installer:startInstallation");
            }
            if (page == "Ready")
            {
                base.NextButton.Header = Translate.Text("Install");
            }
            if (page == "LastPage")
            {
                base.BackButton.Disabled = true;
            }
            if (!this.Successful)
            {
                base.CancelButton.Header = Translate.Text("Close");
                this.Successful = true;
            }
        }

        protected override bool ActivePageChanging(string page, ref string newpage)
        {
            bool flag = base.ActivePageChanging(page, ref newpage);
            if ((page == "LoadPackage") && (newpage == "License"))
            {
                flag = this.LoadPackage();
                if (!this.HasLicense)
                {
                    newpage = "Readme";
                    if (!this.HasReadme)
                    {
                        newpage = "Ready";
                    }
                }
                return flag;
            }
            if ((page == "License") && (newpage == "Readme"))
            {
                if (!this.HasReadme)
                {
                    newpage = "Ready";
                }
                return flag;
            }
            if ((page != "Ready") || (newpage != "Readme"))
            {
                if ((page == "Readme") && ((newpage == "License") && !this.HasLicense))
                {
                    newpage = "LoadPackage";
                }
                return flag;
            }
            if (!this.HasReadme)
            {
                newpage = "License";
                if (!this.HasLicense)
                {
                    newpage = "LoadPackage";
                }
            }
            return flag;
        }

        protected void Agree()
        {
            base.NextButton.Disabled = false;
            Context.ClientPage.ClientResponse.SetReturnValue(true);
        }

        [HandleMessage("installer:browse", true)]
        protected void Browse(ClientPipelineArgs args)
        {
            Assembly assembly = Assembly.LoadFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin\\Sitecore.Client.dll"));
            Type type = assembly.GetType("Sitecore.Shell.Applications.Install.Dialogs.DialogUtils");

            if (type != null)
            {
                MethodInfo methodInfo = type.GetMethod("Browse");
                methodInfo.Invoke(null, new object[] { args, this.PackageFile });
            }
        }

        public void Cancel()
        {
            int index = base.Pages.IndexOf(base.Active);
            if ((index != 0) && (index != (base.Pages.Count - 1)))
            {
                this.Cancelling = true;
                Context.ClientPage.Start(this, "Confirmation");
            }
            else
            {
                this.Cancelling = index == 0;
                this.EndWizard();
            }
        }

        protected void Disagree()
        {
            base.NextButton.Disabled = true;
            Context.ClientPage.ClientResponse.SetReturnValue(true);
        }

        protected void Done()
        {
            base.Active = "LastPage";
            base.BackButton.Disabled = true;
            base.NextButton.Disabled = true;
            base.CancelButton.Disabled = false;
        }

        [HandleMessage("installer:doPostAction")]
        protected void DoPostAction(Message msg)
        {
            if (!string.IsNullOrEmpty(this.PostAction))
            {
                this.StartPostAction();
            }
        }

        protected override void EndWizard()
        {
            if (!this.Cancelling)
            {
                if (this.RestartServer.Checked)
                {
                    SupportInstaller.RestartServer();
                }
                if (this.Restart.Checked)
                {
                    Context.ClientPage.ClientResponse.Broadcast(Context.ClientPage.ClientResponse.SetLocation(string.Empty), "Shell");
                }
            }
            Windows.Close();
        }

        private IProcessingContext GetContextWithMetadata()
        {
            IProcessingContext context = SupportInstaller.CreatePreviewContext();
            MetadataSink sink = new MetadataSink(new MetadataView(context));
            sink.Initialize(context);
            new PackageReader(MainUtil.MapPath(SupportInstaller.GetFilename(this.PackageFile.Value))).Populate(sink);
            return context;
        }

        private static string GetFullDescription(Exception e) =>
            e.ToString();

        private static string GetShortDescription(Exception e)
        {
            string message = e.Message;
            int index = message.IndexOf("(method:", StringComparison.InvariantCulture);
            return ((index <= -1) ? message : message.Substring(0, index - 1));
        }

        private void GotoLastPage(Result result, string shortDescription, string fullDescription)
        {
            this.ErrorDescription.Text = fullDescription;
            this.FailingReason.Text = shortDescription;
            this.Cancelling = result != Result.Success;
            SetVisibility(this.SuccessMessage, result == Result.Success);
            SetVisibility(this.ErrorMessage, result == Result.Failure);
            SetVisibility(this.AbortMessage, result == Result.Abort);
            InstallationEventArgs args = new InstallationEventArgs(new List<ItemUri>(), new List<FileCopyInfo>(), "packageinstall:ended");
            object[] parameters = new object[] { args };
            Event.RaiseEvent("packageinstall:ended", parameters);
            this.Successful = result == Result.Success;
            base.Active = "LastPage";
        }

        private bool LoadPackage()
        {
            string path = this.PackageFile.Value;
            if (Path.GetExtension(path).Trim().Length == 0)
            {
                path = Path.ChangeExtension(path, ".zip");
                this.PackageFile.Value = path;
            }
            if (path.Trim().Length == 0)
            {
                Context.ClientPage.ClientResponse.Alert("Please specify a package.");
                return false;
            }
            path = SupportInstaller.GetFilename(path);
            if (!FileUtil.FileExists(path))
            {
                object[] parameters = new object[] { path };
                Context.ClientPage.ClientResponse.Alert(Translate.Text("The package \"{0}\" file does not exist.", parameters));
                return false;
            }
            IProcessingContext context = SupportInstaller.CreatePreviewContext();
            MetadataView view = new MetadataView(context);
            MetadataSink sink = new MetadataSink(view);
            sink.Initialize(context);
            new PackageReader(MainUtil.MapPath(path)).Populate(sink);
            if ((context == null) || (context.Data == null))
            {
                object[] parameters = new object[] { path };
                Context.ClientPage.ClientResponse.Alert(Translate.Text("The package \"{0}\" could not be loaded.\n\nThe file maybe corrupt.", parameters));
                return false;
            }
            this.PackageVersion = context.Data.ContainsKey("installer-version") ? 2 : 1;
            this.PackageName.Value = view.PackageName;
            this.Version.Value = view.Version;
            this.Author.Value = view.Author;
            this.Publisher.Value = view.Publisher;
            this.LicenseAgreement.InnerHtml = view.License;
            this.ReadmeText.Value = view.Readme;
            this.HasLicense = view.License.Length > 0;
            this.HasReadme = view.Readme.Length > 0;
            this.PostAction = view.PostStep;
            Registry.SetString("Packager/File", this.PackageFile.Value);
            return true;
        }

        private void Monitor_JobDisappeared(object sender, EventArgs e)
        {
            Assert.ArgumentNotNull(sender, "sender");
            Assert.ArgumentNotNull(e, "e");
            object currentStepSync = this.CurrentStepSync;
            lock (currentStepSync)
            {
                InstallationSteps currentStep = this.CurrentStep;
                if (currentStep == InstallationSteps.MainInstallation)
                {
                    this.GotoLastPage(Result.Failure, Translate.Text("Installation could not be completed."), Translate.Text("Installation job was interrupted unexpectedly."));
                }
                else if (currentStep != InstallationSteps.WaitForFiles)
                {
                    this.Monitor_JobFinished(sender, e);
                }
                else
                {
                    this.WatchForInstallationStatus();
                }
            }
        }

        private void Monitor_JobFinished(object sender, EventArgs e)
        {
            Assert.ArgumentNotNull(sender, "sender");
            Assert.ArgumentNotNull(e, "e");
            object currentStepSync = this.CurrentStepSync;
            lock (currentStepSync)
            {
                switch (this.CurrentStep)
                {
                    case InstallationSteps.MainInstallation:
                        this.CurrentStep = InstallationSteps.WaitForFiles;
                        this.WatchForInstallationStatus();
                        break;

                    case InstallationSteps.WaitForFiles:
                        this.CurrentStep = InstallationSteps.InstallSecurity;
                        this.StartInstallingSecurity();
                        break;

                    case InstallationSteps.InstallSecurity:
                        this.CurrentStep = InstallationSteps.RunPostAction;
                        if (string.IsNullOrEmpty(this.PostAction))
                        {
                            this.GotoLastPage(Result.Success, string.Empty, string.Empty);
                        }
                        else
                        {
                            this.StartPostAction();
                        }
                        break;

                    case InstallationSteps.RunPostAction:
                        this.GotoLastPage(Result.Success, string.Empty, string.Empty);
                        break;

                    default:
                        break;
                }
            }
        }

        protected override void OnCancel(object sender, EventArgs formEventArgs)
        {
            this.Cancel();
        }

        [HandleMessage("installer:commitingFiles"), UsedImplicitly]
        private void OnCommittingFiles(Message message)
        {
            Assert.ArgumentNotNull(message, "message");
            object currentStepSync = this.CurrentStepSync;
            lock (currentStepSync)
            {
                if (this.CurrentStep == InstallationSteps.MainInstallation)
                {
                    this.CurrentStep = InstallationSteps.WaitForFiles;
                    this.WatchForInstallationStatus();
                }
            }
        }

        [HandleMessage("installer:aborted")]
        protected void OnInstallerAborted(Message message)
        {
            this.GotoLastPage(Result.Abort, string.Empty, string.Empty);
            this.CurrentStep = InstallationSteps.Failed;
        }

        [HandleMessage("installer:failed")]
        protected void OnInstallerFailed(Message message)
        {
            Job job = JobManager.GetJob(this.Monitor.JobHandle);
            Assert.IsNotNull(job, "Job is not available");
            Exception result = job.Status.Result as Exception;
            Error.AssertNotNull(result, "Cannot get any exception details");
            this.GotoLastPage(Result.Failure, GetShortDescription(result), GetFullDescription(result));
            this.CurrentStep = InstallationSteps.Failed;
        }

        protected override void OnLoad(EventArgs e)
        {
            if (!Context.ClientPage.IsEvent)
            {
                this.OriginalNextButtonHeader = base.NextButton.Header;
            }
            base.OnLoad(e);
            Assembly assembly = Assembly.LoadFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin\\Sitecore.Client.dll"));
            Type type = assembly.GetType("Sitecore.Shell.Applications.Install.Dialogs.DialogUtils");

            if (type != null)
            {
                MethodInfo methodInfo = type.GetMethod("AttachMonitor");
                this.Monitor = (Sitecore.Jobs.AsyncUI.JobMonitor)(methodInfo.Invoke(null, new object[] { this.Monitor }));
            }

            if (!Context.ClientPage.IsEvent)
            {
                this.PackageFile.Value = Registry.GetString("Packager/File");
                this.Decline.Checked = true;
                this.Restart.Checked = true;
                this.RestartServer.Checked = false;
            }
            this.Monitor.JobFinished += new EventHandler(this.Monitor_JobFinished);
            this.Monitor.JobDisappeared += new EventHandler(this.Monitor_JobDisappeared);
            base.WizardCloseConfirmationText = "Are you sure you want to cancel installing a package.";
        }

        protected void RestartInstallation()
        {
            base.Active = "Ready";
            base.CancelButton.Visible = true;
            base.CancelButton.Disabled = false;
            base.NextButton.Visible = true;
            base.NextButton.Disabled = false;
            base.BackButton.Visible = false;
        }

        [HandleMessage("installer:savePostAction")]
        protected void SavePostAction(Message msg)
        {
            string str = msg.Arguments[0];
            this.PostAction = str;
        }

        [HandleMessage("installer:setTaskId"), UsedImplicitly]
        private void SetTaskID(Message message)
        {
            Assert.ArgumentNotNull(message, "message");
            Assert.IsNotNull(message["id"], "id");
            this.MainInstallationTaskID = message["id"];
        }

        private static void SetVisibility(Control control, bool visible)
        {
            Context.ClientPage.ClientResponse.SetStyle(control.ID, "display", visible ? "" : "none");
        }

        [HandleMessage("installer:startInstallation")]
        protected void StartInstallation(Message message)
        {
            Assert.ArgumentNotNull(message, "message");
            this.CurrentStep = InstallationSteps.MainInstallation;
            string filename = SupportInstaller.GetFilename(this.PackageFile.Value);
            if (FileUtil.IsFile(filename))
            {
                this.StartTask(filename);
            }
            else
            {
                Context.ClientPage.ClientResponse.Alert("Package not found");
                base.Active = "Ready";
                base.BackButton.Disabled = true;
            }
        }

        private void StartInstallingSecurity()
        {
            string filename = SupportInstaller.GetFilename(this.PackageFile.Value);
            this.Monitor.Start("InstallSecurity", "Install", new ThreadStart(new AsyncHelper(filename).InstallSecurity));
        }

        private void StartPostAction()
        {
            if (!ReferenceEquals(this.Monitor.JobHandle, Handle.Null))
            {
                Log.Info("Waiting for installation task completion", this);
                SheerResponse.Timer("installer:doPostAction", 100);
            }
            else
            {
                string postAction = this.PostAction;
                this.PostAction = string.Empty;
                if ((postAction.IndexOf("://", StringComparison.InvariantCulture) < 0) && postAction.StartsWith("/", StringComparison.InvariantCulture))
                {
                    postAction = WebUtil.GetServerUrl() + postAction;
                }
                this.Monitor.Start("RunPostAction", "Install", new ThreadStart(new AsyncHelper(postAction, this.GetContextWithMetadata()).ExecutePostStep));
            }
        }

        private void StartTask(string packageFile)
        {
            this.Monitor.Start("Install", "Install", new ThreadStart(new AsyncHelper(packageFile).Install));
        }

        [HandleMessage("installer:upload", true)]
        protected void Upload(ClientPipelineArgs args)
        {
            Assembly assembly = Assembly.LoadFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin\\Sitecore.Client.dll"));
            Type type = assembly.GetType("Sitecore.Shell.Applications.Install.Dialogs.DialogUtils");

            if (type != null)
            {
                MethodInfo methodInfo = type.GetMethod("Upload");
                methodInfo.Invoke(null, new object[] { args, this.PackageFile });
            }
        }

        private void WatchForInstallationStatus()
        {
            string statusFileName = FileInstaller.GetStatusFileName(this.MainInstallationTaskID);
            this.Monitor.Start("WatchStatus", "Install", new ThreadStart(new AsyncHelper().SetStatusFile(statusFileName).WatchForStatus));
        }

        public bool HasLicense
        {
            get
            {
                return MainUtil.GetBool(Context.ClientPage.ServerProperties["HasLicense"], false);
            }

            set
            {
                Context.ClientPage.ServerProperties["HasLicense"] = value.ToString();
            }
        }

        public bool HasReadme
        {
            get
            {
                return MainUtil.GetBool(Context.ClientPage.ServerProperties["Readme"], false);
            }

            set
            {
                Context.ClientPage.ServerProperties["Readme"] = value.ToString();
            }
        }

        private string PostAction
        {
            get
            {
                return StringUtil.GetString(this.ServerProperties["postAction"]);
            }

            set
            {
                this.ServerProperties["postAction"] = value;
            }
        }

        private InstallationSteps CurrentStep
        {
            get
            {
                return ((InstallationSteps)((int)this.ServerProperties["installationStep"]));
            }

            set
            {
                object currentStepSync = this.CurrentStepSync;
                lock (currentStepSync)
                {
                    this.ServerProperties["installationStep"] = (int)value;
                }
            }
        }

        private int PackageVersion
        {
            get
            {
                return int.Parse(StringUtil.GetString(this.ServerProperties["packageType"], "1"));
            }

            set
            {
                this.ServerProperties["packageType"] = value;
            }
        }

        private bool Successful
        {
            get
            {
                object obj2 = this.ServerProperties["Successful"];
                return ((obj2 is bool) ? ((bool)obj2) : true);
            }
            set
            {
                this.ServerProperties["Successful"] = value;
            }
        }

        private string MainInstallationTaskID
        {
            get
            {
                return StringUtil.GetString(this.ServerProperties["taskID"]);
            }

            set
            {
                this.ServerProperties["taskID"] = value;
            }
        }

        private bool Cancelling
        {
            get
            {
                return MainUtil.GetBool(Context.ClientPage.ServerProperties["__cancelling"], false);
            }

            set
            {
                Context.ClientPage.ServerProperties["__cancelling"] = value;
            }
        }

        private string OriginalNextButtonHeader
        {
            get
            {
                return StringUtil.GetString(Context.ClientPage.ServerProperties["next-header"]);
            }

            set
            {
                Context.ClientPage.ServerProperties["next-header"] = value;
            }
        }

        private class AsyncHelper
        {
            private string _packageFile;
            private string _postAction;
            private IProcessingContext _context;
            private StatusFile _statusFile;
            private Language _language;

            public AsyncHelper()
            {
                this._language = Context.Language;
            }

            public AsyncHelper(string package)
            {
                this._packageFile = package;
                this._language = Context.Language;
            }

            public AsyncHelper(string postAction, IProcessingContext context)
            {
                this._postAction = postAction;
                this._context = context;
                this._language = Context.Language;
            }

            private void CatchExceptions(ThreadStart start)
            {
                try
                {
                    start();
                }
                catch (ThreadAbortException)
                {
                    if (!Environment.HasShutdownStarted)
                    {
                        Thread.ResetAbort();
                    }
                    Log.Info("Installation was aborted", this);
                    JobContext.PostMessage("installer:aborted");
                    JobContext.Flush();
                }
                catch (Exception exception)
                {
                    Log.Error("Installation failed: " + exception, this);
                    JobContext.Job.Status.Result = exception;
                    JobContext.PostMessage("installer:failed");
                    JobContext.Flush();
                }
            }

            public void ExecutePostStep()
            {
                this.CatchExceptions(() => new SupportInstaller().ExecutePostStep(this._postAction, this._context));
            }

            public void Install()
            {
                this.CatchExceptions(delegate {
                    using (new SecurityDisabler())
                    {
                        using (new SyncOperationContext())
                        {
                            using (new LanguageSwitcher(this._language))
                            {
                                using (VirtualDrive drive = new VirtualDrive(FileUtil.MapPath(Settings.TempFolderPath)))
                                {
                                    SettingsSwitcher switcher = null;

                                    if (!string.IsNullOrEmpty(drive.Name))
                                    {
                                        switcher = new SettingsSwitcher("TempFolder", drive.Name);
                                    }

                                    try
                                    {
                                        Assembly assembly = Assembly.LoadFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin\\Sitecore.Client.dll"));
                                        Type type = assembly.GetType("Sitecore.Shell.Applications.Install.Dialogs.InstallPackage.UiInstallerEvents");
                                        ConstructorInfo ctor = type.GetConstructor(new Type[] { });
                                        object instance = ctor.Invoke(new Object[0]);

                                        IProcessingContext context = SupportInstaller.CreateInstallationContext();
                                        JobContext.PostMessage("installer:setTaskId(id=" + context.TaskID + ")");
                                        context.AddAspect<IItemInstallerEvents>((IItemInstallerEvents)(ctor.Invoke(new Object[0])));
                                        context.AddAspect<IFileInstallerEvents>((IFileInstallerEvents)(ctor.Invoke(new Object[0])));
                                        //fix bug #373970
                                        new SupportInstaller().InstallPackage(PathUtils.MapPath(this._packageFile), context);
                                    }
                                    finally
                                    {

                                        switcher.Dispose();
                                    }

                                }
                            }
                        }
                    }
                });
            }

            public void InstallSecurity()
            {
                this.CatchExceptions(delegate {
                    using (new LanguageSwitcher(this._language))
                    {
                        Assembly assembly = Assembly.LoadFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin\\Sitecore.Client.dll"));
                        Type type = assembly.GetType("Sitecore.Shell.Applications.Install.Dialogs.InstallPackage.UiInstallerEvents");
                        ConstructorInfo ctor = type.GetConstructor(new Type[] { });
                        object instance = ctor.Invoke(new Object[0]);

                        IProcessingContext context = SupportInstaller.CreateInstallationContext();
                        context.AddAspect<IAccountInstallerEvents>((IAccountInstallerEvents)instance);
                        new SupportInstaller().InstallSecurity(PathUtils.MapPath(this._packageFile), context);
                    }
                });
            }


            public InstallPackageForm.AsyncHelper SetStatusFile(string filename)
            {
                this._statusFile = new StatusFile(filename);
                return this;
            }

            public void WatchForStatus()
            {
                this.CatchExceptions(delegate {
                    Assert.IsNotNull(this._statusFile, "Internal error: status file not set.");
                    bool flag = false;
                    while (true)
                    {
                        StatusFile.StatusInfo info = this._statusFile.ReadStatus();
                        if (info != null)
                        {
                            StatusFile.Status status = info.Status;
                            if (status == StatusFile.Status.Finished)
                            {
                                flag = true;
                            }
                            else if (status == StatusFile.Status.Failed)
                            {
                                throw new Exception("Background process failed: " + info.Exception.Message, info.Exception);
                            }
                            Thread.Sleep(100);
                        }
                        if (flag)
                        {
                            return;
                        }
                    }
                });
            }
        }

        private enum InstallationSteps
        {
            MainInstallation,
            WaitForFiles,
            InstallSecurity,
            RunPostAction,
            None,
            Failed
        }

        private enum Result
        {
            Success,
            Failure,
            Abort
        }
    }
}
