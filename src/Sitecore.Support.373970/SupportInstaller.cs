namespace Sitecore.Support.Install
{ 
    using Sitecore;
    using Sitecore.Configuration;
    using Sitecore.Data;
    using Sitecore.Data.Items;
    using Sitecore.Diagnostics;
    using Sitecore.Events;
    using Sitecore.Globalization;
    using Sitecore.Install.Events;
    using Sitecore.Install.Files;
    using Sitecore.Install.Framework;
    using Sitecore.Install.Items;
    using Sitecore.Install.Metadata;
    using Sitecore.Install.Security;
    using Sitecore.Install.Utils;
    using Sitecore.Install.Zip;
    using Sitecore.IO;
    using Sitecore.Reflection;
    using Sitecore.Web;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection;
    class SupportInstaller : MarshalByRefObject
    {
        public static IProcessingContext CreateInstallationContext()
        {
            return new SimpleProcessingContext();
        }

        public static ISink<PackageEntry> CreateInstallerSink(IProcessingContext context)
        {
            SinkDispatcher dispatcher = new SinkDispatcher(context);
            dispatcher.AddSink(Sitecore.Install.Constants.MetadataPrefix, new MetadataSink(context));
            //fix bug #373970
            dispatcher.AddSink(Sitecore.Install.Constants.BlobDataPrefix, new SupportBlobInstaller(context));
            dispatcher.AddSink(Sitecore.Install.Constants.ItemsPrefix, new LegacyItemUnpacker(new SupportItemInstaller(context)));
            //end of fix
            dispatcher.AddSink(Sitecore.Install.Constants.FilesPrefix, new FileInstaller(context));
            return dispatcher;
        }

        public static IProcessingContext CreatePreviewContext()
        {
            return new SimpleProcessingContext
            {
                SkipData = true,
                SkipErrors = true,
                SkipCompression = true,
                ShowSourceInfo = true
            };
        }

        private Item CreateRegistrationItem(string name)
        {
            Database database = Factory.GetDatabase("core");
            if (database != null)
            {
                TemplateItem folderTemplate = database.Templates[TemplateIDs.Node];
                TemplateItem itemTemplate = database.Templates[TemplateIDs.PackageRegistration];
                if ((folderTemplate != null) && (itemTemplate != null))
                {
                    return database.CreateItemPath("/sitecore/system/Packages/Installation history/" + name + "/" + DateUtil.IsoNow, folderTemplate, itemTemplate);
                }
            }
            return null;
        }

        public void ExecutePostStep(string action, IProcessingContext context)
        {
            if (!string.IsNullOrEmpty(action))
            {
                try
                {
                    InstallationEventArgs args = new InstallationEventArgs(new List<ItemUri>(), new List<FileCopyInfo>(), "packageinstall:poststep:starting");
                    object[] parameters = new object[] { args };
                    Event.RaiseEvent("packageinstall:poststep:starting", parameters);
                    action = action.Trim();
                    if (action.StartsWith("/", StringComparison.InvariantCulture))
                    {
                        action = Globals.ServerUrl + action;
                    }
                    if (action.IndexOf("://", StringComparison.InvariantCulture) > -1)
                    {
                        try
                        {
                            WebUtil.ExecuteWebPage(action);
                        }
                        catch (Exception exception)
                        {
                            Log.Error("Error executing post step for package", exception, this);
                        }
                    }
                    else
                    {
                        object obj2 = null;
                        try
                        {
                            obj2 = ReflectionUtil.CreateObject(action);
                        }
                        catch
                        {
                        }
                        if (obj2 == null)
                        {
                            Log.Error(string.Format("Execution of post step failed: Class '{0}' wasn't found.", action), this);
                        }
                        else if (!(obj2 is IPostStep))
                        {
                            ReflectionUtil.CallMethod(obj2, "RunPostStep");
                        }
                        else
                        {
                            ITaskOutput output = context.Output;
                            (obj2 as IPostStep).Run(output, new MetadataView(context).Metadata);
                        }
                    }
                }
                finally
                {
                    InstallationEventArgs args2 = new InstallationEventArgs(new List<ItemUri>(), new List<FileCopyInfo>(), "packageinstall:poststep:ended");
                    object[] parameters = new object[] { args2 };
                    Event.RaiseEvent("packageinstall:poststep:ended", parameters);
                    InstallationEventArgs args3 = new InstallationEventArgs(new List<ItemUri>(), new List<FileCopyInfo>(), "packageinstall:ended");
                    object[] objArray3 = new object[] { args3 };
                    Event.RaiseEvent("packageinstall:ended", objArray3);
                }
            }
        }

        public static string GetFilename(string filename)
        {
            Error.AssertString(filename, "filename", true);
            string str = filename;
            if (!FileUtil.IsFullyQualified(str))
            {
                str = FileUtil.MakePath(Settings.PackagePath, str);
            }
            return str;
        }

        public static string GetPostStep(IProcessingContext context)
        {
            string[] values = new string[] { new MetadataView(context).PostStep };
            return StringUtil.GetString(values);
        }

        public void InstallPackage(string path)
        {
            this.InstallPackage(path, CreateInstallationContext());
        }

        public void InstallPackage(string path, IProcessingContext context)
        {
            ISource<PackageEntry> source = new PackageReader(path);
            this.InstallPackage(path, source, context);
        }

        public void InstallPackage(string path, ISource<PackageEntry> source)
        {
            this.InstallPackage(path, source, CreateInstallationContext());
        }

        public void InstallPackage(string path, bool registerInstallation)
        {
            this.InstallPackage(path, registerInstallation, CreateInstallationContext());
        }

        public void InstallPackage(string path, ISource<PackageEntry> source, IProcessingContext context)
        {
            this.InstallPackage(path, true, source, context);
        }

        public void InstallPackage(string path, bool registerInstallation, IProcessingContext context)
        {
            ISource<PackageEntry> source = new PackageReader(path);
            this.InstallPackage(path, registerInstallation, source, context);
        }

        public void InstallPackage(string path, bool registerInstallation, ISource<PackageEntry> source)
        {
            this.InstallPackage(path, registerInstallation, source, CreateInstallationContext());
        }

        public void InstallPackage(string path, bool registerInstallation, ISource<PackageEntry> source, IProcessingContext context)
        {
            InstallationEventArgs args = new InstallationEventArgs(null, null, "packageinstall:starting");
            object[] parameters = new object[] { args };
            Event.RaiseEvent("packageinstall:starting", parameters);
            Log.Info("Installing package: " + path, this);

            Assembly assembly = Assembly.LoadFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin\\Sitecore.Kernel.dll"));
            Type type = assembly.GetType("Sitecore.Install.PackageInstallationContext");
            ConstructorInfo ctor = type.GetConstructor(new Type[] { });

            using ((IDisposable)(ctor.Invoke(new Object[0])))
            {
                using (ConfigWatcher.PostponeEvents())
                {
                    ISink<PackageEntry> sink = CreateInstallerSink(context);
                    new EntrySorter(source).Populate(sink);
                    sink.Flush();
                    sink.Finish();
                    if (registerInstallation)
                    {
                        this.RegisterPackage(context);
                    }
                    foreach (IProcessor<IProcessingContext> processor in context.PostActions)
                    {
                        processor.Process(context, context);
                    }
                }
            }
        }

        public void InstallSecurity(string path)
        {
            Assert.ArgumentNotNullOrEmpty(path, "path");
            this.InstallSecurity(path, new SimpleProcessingContext());
        }

        public void InstallSecurity(string path, IProcessingContext context)
        {
            Assert.ArgumentNotNullOrEmpty(path, "path");
            Assert.ArgumentNotNull(context, "context");
            Log.Info("Installing security from package: " + path, this);
            PackageReader reader = new PackageReader(path);
            AccountInstaller sink = new AccountInstaller();
            sink.Initialize(context);
            reader.Populate(sink);
            sink.Flush();
            sink.Finish();
        }

        protected virtual void RegisterPackage(IProcessingContext context)
        {
            bool flag;
            Assert.ArgumentNotNull(context, "context");
            MetadataView view = new MetadataView(context);
            string packageName = view.PackageName;
            try
            {
                flag = ItemUtil.IsItemNameValid(packageName);
            }
            catch (Exception)
            {
                flag = false;
            }
            if (!flag && (packageName.Length > 0))
            {
                packageName = ItemUtil.ProposeValidItemName(packageName);
            }
            if (packageName.Length == 0)
            {
                packageName = Translate.Text("Unnamed Package");
            }
            Item item = this.CreateRegistrationItem(packageName);
            if (item == null)
            {
                Log.Error("Could not get registration item for package: " + packageName, this);
            }
            else
            {
                item.Editing.BeginEdit();
                item[Sitecore.Install.PackageRegistrationFieldIDs.PackageName] = view.PackageName;
                item[Sitecore.Install.PackageRegistrationFieldIDs.PackageID] = view.PackageID;
                item[Sitecore.Install.PackageRegistrationFieldIDs.PackageVersion] = view.Version;
                item[Sitecore.Install.PackageRegistrationFieldIDs.PackageAuthor] = view.Author;
                item[Sitecore.Install.PackageRegistrationFieldIDs.PackagePublisher] = view.Publisher;
                item[Sitecore.Install.PackageRegistrationFieldIDs.PackageReadme] = view.Readme;
                item[Sitecore.Install.PackageRegistrationFieldIDs.PackageRevision] = view.Revision;
                item.Editing.EndEdit();
            }
        }

        public static void RestartServer()
        {
            new FileInfo(FileUtil.MapPath("/web.config")).LastWriteTimeUtc = DateTime.UtcNow;
        }
    }
}
