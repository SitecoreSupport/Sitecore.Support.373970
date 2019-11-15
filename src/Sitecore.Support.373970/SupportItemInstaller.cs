namespace Sitecore.Support.Install
{
    using Sitecore.Install.Framework;
    using Sitecore;
    using Sitecore.Collections;
    using Sitecore.Configuration;
    using Sitecore.Data;
    using Sitecore.Data.Items;
    using Sitecore.Data.Managers;
    using Sitecore.Diagnostics;
    using Sitecore.Events;
    using Sitecore.Globalization;
    using Sitecore.Install.Events;
    using Sitecore.Install.Utils;
    using Sitecore.SecurityModel;
    using Sitecore.Xml;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Xml;
    using Sitecore.Install.Items;
    using Sitecore.Install.Files;

    public class SupportItemInstaller : AdvancedBaseSink<PackageEntry, ItemInstallerContext>
    {
        private static Func<ItemReference, XmlVersionParser, Sitecore.Data.Items.Item, DateTime, Sitecore.Data.Items.Item> createItem = (Func<ItemReference, XmlVersionParser, Sitecore.Data.Items.Item, DateTime, Sitecore.Data.Items.Item>)((item, parser, parent, created) => ItemManager.CreateItem(parser.Name, parent, parser.TemplateID, item.ID, created, SecurityCheck.Disable));
        private readonly SafeDictionary<string, List<ID>> _IDsAlreadyInstalled = new SafeDictionary<string, List<ID>>();
        private readonly SafeDictionary<string, List<ID>> _IDsToBeInstalled = new SafeDictionary<string, List<ID>>();
        private readonly List<PackageEntry> _installationQueue = new List<PackageEntry>();
        private readonly IList<KeyValuePair<string, string>> _pendingDeleteItems = (IList<KeyValuePair<string, string>>)new List<KeyValuePair<string, string>>();
        /// <summary>
        /// Contains entries that should be removed from deletion queue.
        /// </summary>
        private readonly IList<KeyValuePair<string, string>> removeFromDeletionQueue = (IList<KeyValuePair<string, string>>)new List<KeyValuePair<string, string>>();
        /// <summary>Contains identifiers of created items.</summary>
        private readonly HashSet<ID> createdItems = new HashSet<ID>();
        /// <summary>Contains entries that have not installed template yet</summary>
        private readonly List<PackageEntry> _postponedToInstall = new List<PackageEntry>();
        private List<ItemUri> installedItems = new List<ItemUri>();
        private readonly SafeDictionary<PackageEntry, XmlVersionParser> _parserCache = new SafeDictionary<PackageEntry, XmlVersionParser>();

        /// <summary>
        /// Initializes a new instance of the <see cref="T:Sitecore.Install.Items.ItemInstaller" /> class.
        /// </summary>
        /// <param name="context">The context.</param>
        public SupportItemInstaller(IProcessingContext context)
        {
            this.Initialize(context);
        }

        /// <summary>Finishes the sink.</summary>
        public override void Finish()
        {
            this.ProcessingContext.PostActions.Add((IProcessor<IProcessingContext>)new SupportItemInstaller.ContentRestorer(this.PendingDeleteItems));
        }

        /// <summary>Flushes the sink</summary>
        public override void Flush()
        {
            if (this._installationQueue.Count == 0)
                return;
            try
            {
                while (true)
                {
                    this._postponedToInstall.Clear();
                    int count = this._installationQueue.Count;
                    foreach (PackageEntry installation in this._installationQueue)
                        this.InstallEntry(installation);
                    Event.RaiseEvent("packageinstall:items:starting", (object)new InstallationEventArgs((IEnumerable<ItemUri>)this.installedItems, (IEnumerable<FileCopyInfo>)new List<FileCopyInfo>(), "packageinstall:items:starting"));
                    this._installationQueue.Clear();
                    if (this._postponedToInstall.Count != 0)
                    {
                        if (this._postponedToInstall.Count != count)
                            this._installationQueue.AddRange((IEnumerable<PackageEntry>)this._postponedToInstall);
                        else
                            throw new Exception("Cannot install templates structure. There're some cyclic references or some template is under an item having been created by that template");
                    }
                    else
                        break;
                }
                return;
            }
            finally
            {
                this.ClearXmlParserCache();
                this._installationQueue.Clear();
                this._IDsToBeInstalled.Clear();
                this._IDsAlreadyInstalled.Clear();
                this.removeFromDeletionQueue.Clear();
                this.createdItems.Clear();
                Event.RaiseEvent("packageinstall:items:ended", (object)new InstallationEventArgs((IEnumerable<ItemUri>)this.installedItems, (IEnumerable<FileCopyInfo>)null, "packageinstall:items:ended"));
                this.installedItems.Clear();
                SupportBlobInstaller.FlushData(this.ProcessingContext);
            }
        }

        /// <summary>
        /// If an entry contains an item than that item will be added to the installation queue.
        /// </summary>
        /// <param name="entry">The entry.</param>
        public override void Put(PackageEntry entry)
        {
            ItemReference reference = ItemKeyUtils.GetReference(entry.Key);
            if (reference == null)
            {
                Log.Warn("Invalid entry key encountered during installation: " + entry.Key, (object)this);
            }
            else
            {
                string databaseName = reference.DatabaseName;
                if (this._IDsToBeInstalled[databaseName] == null)
                    this._IDsToBeInstalled[databaseName] = new List<ID>();
                List<ID> idList = this._IDsToBeInstalled[databaseName];
                if (!idList.Contains(reference.ID))
                    idList.Add(reference.ID);
                this._installationQueue.Add(entry);
            }
        }

        private bool ItemIsInPackage(Sitecore.Data.Items.Item item)
        {
            string name = item.Database.Name;
            return this._IDsToBeInstalled.ContainsKey(name) && this._IDsToBeInstalled[name].Contains(item.ID) || this._IDsAlreadyInstalled.ContainsKey(name) && this._IDsAlreadyInstalled[name].Contains(item.ID);
        }

        /// <summary>Creates the lightweight item.</summary>
        /// <param name="item">The item.</param>
        /// <param name="parser">The parser.</param>
        /// <returns>The lightweight item.</returns>
        protected static Sitecore.Data.Items.Item CreateLightweightItem(
          ItemReference item,
          XmlVersionParser parser)
        {
            string path = item.Path;
            Database database = item.Database;
            Sitecore.Data.Items.Item obj1 = (Sitecore.Data.Items.Item)null;
            if (path.Length > 0 && database != null)
            {
                Sitecore.Data.Items.Item obj2 = database.GetItem(parser.ParentID) ?? GetParentItem(path, database);
                if (obj2 != null)
                {
                    DateTime defaultValue = parser.Created;
                    if (defaultValue == DateTime.MinValue)
                        defaultValue = DateUtil.IsoDateToDateTime(XmlUtil.GetValue(parser.Xml.DocumentElement.SelectSingleNode("fields/field[@key='__created']/content")), defaultValue);
                    obj1 = CreateItem(item, parser, obj2, defaultValue);
                    if (obj1 == null)
                    {
                        if (obj2.Database.GetItem(parser.TemplateID) == null)
                            throw new Exception(string.Format("Failed to add an item. Key: '{0}'. Reason: there's no template with the following ID '{1}'", (object)item.Path, (object)parser.TemplateID));
                        throw new Exception(string.Format("Could not create item. Name: '{0}', ID: '{1}', TemplateID: '{2}', parentId: '{3}'", (object)parser.Name, (object)item.ID, (object)parser.TemplateID, (object)obj2.ID));
                    }
                    obj1.Versions.RemoveAll(true);
                }
                else
                    throw new Exception("Could not find target item for: " + path + " (db: " + database.Name + ")");
            }
            return obj1;
        }

        /// <summary>Gets the item install options.</summary>
        /// <param name="entry">The entry.</param>
        /// <param name="context">The context.</param>
        /// <param name="prefix">The prefix.</param>
        /// <returns>The item install options.</returns>
        protected virtual BehaviourOptions GetItemInstallOptions(
          PackageEntry entry,
          ItemInstallerContext context,
          string prefix)
        {
            BehaviourOptions behaviourOptions = new BehaviourOptions(entry.Properties, prefix);
            if (!behaviourOptions.IsDefined && context.IsApplyToAll(prefix))
            {
                behaviourOptions = context.GetInstallOptions(prefix);
                if (!behaviourOptions.IsDefined)
                    Log.Error("Installer internal error: item install options not saved after apply-to-all", typeof(ItemInstaller));
            }
            return behaviourOptions;
        }

        /// <summary>Gets the prefix.</summary>
        /// <param name="installedItem">The installed item.</param>
        /// <param name="newItem">The new item.</param>
        /// <returns>The prefix.</returns>
        protected static string GetPrefix(Sitecore.Install.Utils.ItemInfo installedItem, Sitecore.Install.Utils.ItemInfo newItem)
        {
            if (!installedItem.ID.Equals(newItem.ID))
                return Sitecore.Install.Constants.PathCollisionPrefix;
            return Sitecore.Install.Constants.IDCollisionPrefix;
        }

        /// <summary>Installs the item.</summary>
        /// <param name="installOptions">The install options.</param>
        /// <param name="targetItem">The target item.</param>
        /// <param name="item">The item.</param>
        /// <param name="parser">The parser.</param>
        protected void InstallItem(
          BehaviourOptions installOptions,
          Sitecore.Data.Items.Item targetItem,
          ItemReference item,
          XmlVersionParser parser)
        {
            bool removeVersions;
            this.InstallItem(installOptions, targetItem, item, parser, out removeVersions);
            if (!removeVersions)
                return;
            RemoveVersions(targetItem, true);
        }

        /// <summary>Installs the item.</summary>
        /// <param name="installOptions">The install options.</param>
        /// <param name="targetItem">The target item.</param>
        /// <param name="item">The item.</param>
        /// <param name="parser">The parser.</param>
        /// <param name="removeVersions">Boolean value indicating whether versions of item should be removed.</param>
        protected void InstallItem(
          BehaviourOptions installOptions,
          Sitecore.Data.Items.Item targetItem,
          ItemReference item,
          XmlVersionParser parser,
          out bool removeVersions)
        {
            removeVersions = false;
            if (targetItem != null)
            {
                this.RemoveFromDeletionQueue(targetItem);
                switch (installOptions.ItemMode)
                {
                    case InstallMode.Undefined:
                        Sitecore.Diagnostics.Error.Assert(false, "Item Install mode is undefined");
                        break;
                    case InstallMode.Overwrite:
                        if (targetItem.ID.Equals((object)item.ID) || targetItem.TemplateID.Equals((object)TemplateIDs.Language))
                        {
                            if (item.Path != targetItem.Paths.FullPath)
                                targetItem.MoveTo(GetParentItem(item.Path, targetItem.Database));
                            removeVersions = true;
                            this.EnqueueChildrenForRemove(targetItem);
                            UpdateItemDefinition(targetItem, parser);
                            break;
                        }
                        targetItem.Delete();
                        CreateLightweightItem(item, parser);
                        break;
                    case InstallMode.Merge:
                        switch (installOptions.ItemMergeMode)
                        {
                            case MergeMode.Undefined:
                                Sitecore.Diagnostics.Error.Assert(false, "Item merge mode is undefined");
                                return;
                            case MergeMode.Clear:
                                removeVersions = true;
                                return;
                            case MergeMode.Append:
                                return;
                            case MergeMode.Merge:
                                return;
                            default:
                                return;
                        }
                    case InstallMode.SideBySide:
                        CreateLightweightItem(item, parser);
                        break;
                }
            }
            else
                this.RemoveFromDeletionQueue(CreateLightweightItem(item, parser));
        }

        /// <summary>Removes the versions.</summary>
        /// <param name="targetItem">The target item.</param>
        /// <param name="removeSharedData">if set to <c>true</c> the shared data will be removed.</param>
        protected static void RemoveVersions(Sitecore.Data.Items.Item targetItem, bool removeSharedData)
        {
            targetItem.Database.Engines.DataEngine.RemoveData(targetItem.ID, Language.Invariant, removeSharedData);
        }

        private void AddToParserCache(PackageEntry entry, XmlVersionParser parser)
        {
            if (this._parserCache.ContainsKey(entry))
                return;
            this._parserCache.Add(entry, parser);
        }

        private void ClearXmlParserCache()
        {
            this._parserCache.Clear();
        }

        private XmlVersionParser GetXmlVersionParser(PackageEntry entry)
        {
            if (this._parserCache.ContainsKey(entry))
                return this._parserCache[entry];
            return new XmlVersionParser(entry);
        }

        private void AddToPostponedList(PackageEntry entry)
        {
            this._postponedToInstall.Add(entry);
        }

        private BehaviourOptions AskUserForInstallOptions(
          Sitecore.Install.Utils.ItemInfo installedItem,
          Sitecore.Install.Utils.ItemInfo newItem,
          ItemInstallerContext context)
        {
            try
            {
                Pair<BehaviourOptions, bool> pair = context.Events.AskUser(installedItem, newItem, this.ProcessingContext);
                string prefix = GetPrefix(installedItem, newItem);
                context.ApplyToAll[prefix] = pair.Part2;
                context.GetInstallOptions(prefix).Assign(pair.Part1);
                return pair.Part1;
            }
            catch (ThreadAbortException ex)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new Exception("Could not query outer context (or user) for overwrite options. Most probably ItemInstallerContext.Events is not set", ex);
            }
        }

        private static KeyValuePair<string, string> BuildCollectionKey(Sitecore.Data.Items.Item source)
        {
            return new KeyValuePair<string, string>(source.Database.Name, source.ID.ToString());
        }

        private void EnqueueChildrenForRemove(Sitecore.Data.Items.Item parentItem)
        {
            foreach (Sitecore.Data.Items.Item child in parentItem.GetChildren(ChildListOptions.IgnoreSecurity))
            {
                KeyValuePair<string, string> collectionKey = BuildCollectionKey(child);
                if (!this._pendingDeleteItems.Any<KeyValuePair<string, string>>((Func<KeyValuePair<string, string>, bool>)(i =>
                {
                    if (i.Key == collectionKey.Key)
                        return i.Value == collectionKey.Value;
                    return false;
                })) && !this.removeFromDeletionQueue.Any<KeyValuePair<string, string>>((Func<KeyValuePair<string, string>, bool>)(i =>
                {
                    if (i.Key == collectionKey.Key)
                        return i.Value == collectionKey.Value;
                    return false;
                })))
                    this._pendingDeleteItems.Add(collectionKey);
            }
        }

        private static string GetKey(ItemReference itemRef)
        {
            return itemRef.DatabaseName + ":" + itemRef.Path;
        }

        private static Sitecore.Data.Items.Item GetParentItem(string path, Database database)
        {
            if (database != null)
            {
                string longestPrefix = StringUtil.GetLongestPrefix(path, Sitecore.Install.Constants.KeySeparator);
                if (longestPrefix.Length > 0)
                    return database.CreateItemPath(longestPrefix);
            }
            return (Sitecore.Data.Items.Item)null;
        }

        /// <summary>Gets the version install mode.</summary>
        /// <param name="entry">The entry.</param>
        /// <param name="reference">The reference.</param>
        /// <param name="parser">The parser.</param>
        /// <param name="context">The context.</param>
        /// <param name="removeVersions">Boolean value indicating whether versions of item should be removed.</param>
        /// <returns>The version install mode.</returns>
        private VersionInstallMode GetVersionInstallMode(
          PackageEntry entry,
          ItemReference reference,
          XmlVersionParser parser,
          ItemInstallerContext context,
          out bool removeVersions)
        {
            removeVersions = false;
            if (this.createdItems.Contains(reference.ID))
            {
                bool ignorePathCollision;
                Sitecore.Data.Items.Item targetItem = this.GetTargetItem(reference, out ignorePathCollision);
                if (targetItem != null)
                {
                    this.RemoveFromDeletionQueue(targetItem);
                    context.VersionInstallMode = VersionInstallMode.Append;
                    return context.VersionInstallMode;
                }
            }
            Sitecore.Install.Utils.ItemInfo newItem = new Sitecore.Install.Utils.ItemInfo(reference, parser);
            if (!newItem.ID.Equals(context.CurrentItemID))
            {
                bool ignorePathCollision;
                Sitecore.Data.Items.Item targetItem = this.GetTargetItem(reference, out ignorePathCollision);
                if (targetItem != null)
                {
                    Sitecore.Install.Utils.ItemInfo installedItem = new Sitecore.Install.Utils.ItemInfo(targetItem);
                    string prefix = GetPrefix(installedItem, newItem);
                    BehaviourOptions installOptions = this.GetItemInstallOptions(entry, context, prefix);
                    if (!installOptions.IsDefined)
                        installOptions = this.AskUserForInstallOptions(installedItem, newItem, context);
                    context.VersionInstallMode = installOptions.GetVersionInstallMode();
                    this.InstallItem(installOptions, targetItem, reference, parser, out removeVersions);
                }
                else
                {
                    context.VersionInstallMode = VersionInstallMode.Append;
                    this.InstallItem(new BehaviourOptions(ignorePathCollision ? InstallMode.SideBySide : InstallMode.Overwrite, MergeMode.Undefined), (Sitecore.Data.Items.Item)null, reference, parser, out removeVersions);
                }
                context.CurrentItemID = targetItem != null ? targetItem.ID.ToString() : newItem.ID;
                string databaseName = reference.DatabaseName;
                if (!this._IDsAlreadyInstalled.ContainsKey(databaseName))
                    this._IDsAlreadyInstalled.Add(databaseName, new List<ID>());
                this._IDsAlreadyInstalled[databaseName].Add(ID.Parse(newItem.ID));
                if (targetItem != null && !this._IDsAlreadyInstalled[databaseName].Contains(targetItem.ID))
                    this._IDsAlreadyInstalled[databaseName].Add(targetItem.ID);
            }
            return context.VersionInstallMode;
        }

        /// <summary>Installs the entry.</summary>
        /// <param name="entry">The entry.</param>
        /// <exception cref="T:System.Exception">ItemInstallerContext is not set in current processing context.</exception>
        private void InstallEntry(PackageEntry entry)
        {
            ItemReference reference = ItemKeyUtils.GetReference(entry.Key).Reduce();
            XmlVersionParser xmlVersionParser = this.GetXmlVersionParser(entry);
            if (xmlVersionParser.TemplateID != reference.ID && (this.ItemIsFurtherInList(xmlVersionParser.TemplateID, reference.DatabaseName) || this.BaseTemplateIsFurtherInList(xmlVersionParser.BaseTemplates, reference.DatabaseName)))
            {
                bool ignorePathCollision;
                if (this.GetTargetItem(reference, out ignorePathCollision) == null)
                {
                    CreateLightweightItem(reference, xmlVersionParser);
                    this.createdItems.Add(reference.ID);
                }
                this.AddToParserCache(entry, xmlVersionParser);
                this.AddToPostponedList(entry);
            }
            else
            {
                if (this.Context == null)
                    throw new Exception("ItemInstallerContext is not set in current processing context.");
                try
                {
                    bool removeVersions;
                    VersionInstallMode versionInstallMode = this.GetVersionInstallMode(entry, reference, xmlVersionParser, this.Context, out removeVersions);
                    switch (versionInstallMode)
                    {
                        case VersionInstallMode.Skip:
                            break;
                        case VersionInstallMode.Undefined:
                            Log.Info(string.Format("Version install mode is not defined for entry '{0}'", (object)entry.Key), (object)this);
                            break;
                        default:
                            Log.Info("Installing item: " + entry.Key, (object)this);
                            ItemReference itemReference = ParseRef(entry.Key);
                            this.installedItems.Add(new ItemUri(ID.Parse(this.Context.CurrentItemID), itemReference.Language, itemReference.Version, itemReference.Database));
                            VersionInstaller.PasteVersion((XmlNode)xmlVersionParser.Xml.DocumentElement, reference.GetItem(), versionInstallMode, this.ProcessingContext, removeVersions);
                            break;
                    }
                }
                catch (ThreadAbortException ex)
                {
                    Log.Info("Installation was aborted at entry: " + entry.Key, (object)this);
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Error("Error installing " + entry.Key, ex, (object)this);
                    throw;
                }
                this._IDsToBeInstalled[reference.DatabaseName].Remove(reference.ID);
            }
        }

        private bool BaseTemplateIsFurtherInList(ID[] baseTemplates, string databaseName)
        {
            if (this._IDsToBeInstalled.ContainsKey(databaseName))
                return ((IEnumerable<ID>)baseTemplates).Any<ID>((Func<ID, bool>)(id => this._IDsToBeInstalled[databaseName].Contains(id)));
            return false;
        }

        private bool ItemIsFurtherInList(ID itemID, string databaseName)
        {
            if (this._IDsToBeInstalled.ContainsKey(databaseName))
                return this._IDsToBeInstalled[databaseName].Contains(itemID);
            return false;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="reference"></param>
        /// <returns></returns>
        public static ItemReference ParseRef(string reference)
        {
            string[] strArray1 = reference.Split('/');
            string database = strArray1[1];
            string str1 = strArray1[strArray1.Length - 2];
            string str2 = strArray1[strArray1.Length - 3];
            string str3 = strArray1[strArray1.Length - 4];
            string[] strArray2 = new string[strArray1.Length - 5];
            strArray2[0] = strArray1[0];
            Array.Copy((Array)strArray1, 2, (Array)strArray2, 1, strArray1.Length - 6);
            string path = string.Join("/", strArray2);
            Language language = string.Compare(str2, "invariant", StringComparison.InvariantCultureIgnoreCase) == 0 ? Language.Invariant : Language.Parse(str2);
            return new ItemReference(database, path, str3.Length == 0 ? (ID)null : ID.Parse(str3), language, Sitecore.Data.Version.Parse(str1));
        }

        /// <summary>
        /// This method returns an item by a reference, but ignores items from the current package which may cause a path collision.
        /// </summary>
        /// <param name="reference">The reference.</param>
        /// <param name="ignorePathCollision">if set to <c>true</c> a path collision should be ignored.</param>
        /// <returns></returns>
        private Sitecore.Data.Items.Item GetTargetItem(
          ItemReference reference,
          out bool ignorePathCollision)
        {
            ignorePathCollision = false;
            Sitecore.Data.Items.Item obj = reference.GetItem();
            if (obj == null || obj.ID == reference.ID || (obj.Parent == null || !this.ItemIsInPackage(obj)))
                return obj;
            foreach (Sitecore.Data.Items.Item child in obj.Parent.Children)
            {
                if (!(child.Name != obj.Name) && !(child.ID == obj.ID) && !this.ItemIsInPackage(child))
                {
                    ignorePathCollision = true;
                    return child;
                }
            }
            return (Sitecore.Data.Items.Item)null;
        }

        private void RemoveFromDeletionQueue(Sitecore.Data.Items.Item item)
        {
            KeyValuePair<string, string> keyValuePair = BuildCollectionKey(item);
            this._pendingDeleteItems.Remove(keyValuePair);
            this.removeFromDeletionQueue.Add(keyValuePair);
        }

        private static void UpdateItemDefinition(Sitecore.Data.Items.Item targetItem, XmlVersionParser parser)
        {
            Sitecore.Data.Items.Item itemVersion = VersionInstaller.ParseItemVersion((XmlNode)parser.Xml.DocumentElement, targetItem, VersionInstallMode.Undefined);
            targetItem.Editing.BeginEdit();
            targetItem.Name = itemVersion.Name;
            targetItem.BranchId = itemVersion.BranchId;
            targetItem.TemplateID = itemVersion.TemplateID;
            targetItem.RuntimeSettings.ReadOnlyStatistics = true;
            targetItem.Editing.EndEdit();
        }

        internal static Func<ItemReference, XmlVersionParser, Sitecore.Data.Items.Item, DateTime, Sitecore.Data.Items.Item> CreateItem
        {
            get
            {
                return createItem;
            }
            set
            {
                createItem = value;
            }
        }

        /// <summary>Gets the pended to be deleted items.</summary>
        /// <value>The pending delete items.</value>
        public IList<KeyValuePair<string, string>> PendingDeleteItems
        {
            get
            {
                return this._pendingDeleteItems;
            }
        }

        /// <summary>Creates the context.</summary>
        /// <returns>The context.</returns>
        protected override ItemInstallerContext CreateContext()
        {
            return new ItemInstallerContext(this.ProcessingContext.HasAspect<IItemInstallerEvents>() ? this.ProcessingContext.GetAspect<IItemInstallerEvents>() : (IItemInstallerEvents)new DefaultItemInstallerEvents(new BehaviourOptions(InstallMode.Merge, MergeMode.Clear)));
        }

        /// <summary>
        /// Content restorer. Deletes item which is pended to be deleted
        /// </summary>
        private class ContentRestorer : IProcessor<IProcessingContext>, IDisposable
        {
            private readonly IList<KeyValuePair<string, string>> pendingDeleteItems;

            /// <summary>
            /// Initializes a new instance of the <see cref="T:Sitecore.Install.Items.ItemInstaller.ContentRestorer" /> class.
            /// </summary>
            /// <param name="pendingDeleteItems">The pending delete items.</param>
            public ContentRestorer(
              IList<KeyValuePair<string, string>> pendingDeleteItems)
            {
                this.pendingDeleteItems = pendingDeleteItems;
            }

            /// <summary>
            /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
            /// </summary>
            public void Dispose()
            {
                this.pendingDeleteItems.Clear();
            }

            /// <summary>Runs the processor.</summary>
            /// <param name="entry">The entry.</param>
            /// <param name="context">The processing context.</param>
            public void Process(IProcessingContext entry, IProcessingContext context)
            {
                foreach (KeyValuePair<string, string> pendingDeleteItem in (IEnumerable<KeyValuePair<string, string>>)this.pendingDeleteItems)
                {
                    try
                    {
                        Database database = Factory.GetDatabase(pendingDeleteItem.Key);
                        if (database != null)
                        {
                            Sitecore.Data.Items.Item obj = database.Items[pendingDeleteItem.Value];
                            if (obj != null)
                                obj.Delete();
                            else
                                Log.Error("Error finding item: [" + pendingDeleteItem.Key + "]: " + pendingDeleteItem.Value, (object)this);
                        }
                        else
                            Log.Error("Error finding database: [" + pendingDeleteItem.Key + "]", (object)this);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Error deleting item: [" + pendingDeleteItem.Key + "]: " + pendingDeleteItem.Value, (object)this);
                    }
                }
            }
        }

        /// <summary>Version installer</summary>
        public static class VersionInstaller
        {
            /// <summary>Pastes the version.</summary>
            /// <param name="versionXml">The version XML.</param>
            /// <param name="target">The target.</param>
            /// <param name="mode">The mode.</param>
            /// <param name="context">The context.</param>
            public static void PasteVersion(
              XmlNode versionXml,
              Sitecore.Data.Items.Item target,
              VersionInstallMode mode,
              IProcessingContext context)
            {
                VersionInstaller.PasteVersion(versionXml, target, mode, context, false);
            }

            /// <summary>Pastes the version.</summary>
            /// <param name="versionXml">The version XML.</param>
            /// <param name="target">The target.</param>
            /// <param name="mode">The mode.</param>
            /// <param name="context">The context.</param>
            /// <param name="removeOtherVersions">if set to <c>true</c> other versions will be removed.</param>
            public static void PasteVersion(
              XmlNode versionXml,
              Sitecore.Data.Items.Item target,
              VersionInstallMode mode,
              IProcessingContext context,
              bool removeOtherVersions)
            {
                Sitecore.Diagnostics.Error.AssertObject((object)versionXml, "xml");
                Sitecore.Diagnostics.Error.AssertObject((object)target, nameof(target));
                Sitecore.Diagnostics.Error.Assert(mode == VersionInstallMode.Append || mode == VersionInstallMode.Merge, "Unknown version install mode");
                Sitecore.Data.Items.Item itemVersion = VersionInstaller.ParseItemVersion(versionXml, target, mode, removeOtherVersions);
                Sitecore.Diagnostics.Error.AssertObject((object)itemVersion, "versions");
                //fix bug #373970
                SupportBlobInstaller.UpdateBlobData(itemVersion, context);
                //end of fix
                VersionInstaller.UpdateFieldSharing(itemVersion, target);
                VersionInstaller.InstallVersion(itemVersion);
                if (!removeOtherVersions)
                    return;
                foreach (Sitecore.Data.Items.Item version in target.Database.GetItem(itemVersion.ID).Versions.GetVersions(true))
                {
                    if (version.Version != itemVersion.Version || version.Language != itemVersion.Language)
                        version.Versions.RemoveVersion();
                }
            }

            /// <summary>Parses the item version.</summary>
            /// <param name="versionNode">The version node.</param>
            /// <param name="target">The target item.</param>
            /// <param name="mode">The version install mode.</param>
            /// <returns>The item version.</returns>
            public static Sitecore.Data.Items.Item ParseItemVersion(
              XmlNode versionNode,
              Sitecore.Data.Items.Item target,
              VersionInstallMode mode)
            {
                return VersionInstaller.ParseItemVersion(versionNode, target, mode, false);
            }

            /// <summary>Parses the item version.</summary>
            /// <param name="versionNode">The version node.</param>
            /// <param name="target">The target item.</param>
            /// <param name="mode">The version install mode.</param>
            /// <param name="removeOtherVersions">if set to <c>true</c> other versions will be removed.</param>
            /// <returns>The item version.</returns>
            public static Sitecore.Data.Items.Item ParseItemVersion(
              XmlNode versionNode,
              Sitecore.Data.Items.Item target,
              VersionInstallMode mode,
              bool removeOtherVersions)
            {
                string attribute1 = XmlUtil.GetAttribute("name", versionNode);
                string attribute2 = XmlUtil.GetAttribute("language", versionNode);
                string str = XmlUtil.GetAttribute("version", versionNode);
                ID id1 = MainUtil.GetID((object)XmlUtil.GetAttribute("tid", versionNode));
                ID id2 = MainUtil.GetID((object)XmlUtil.GetAttribute("mid", versionNode));
                if (ID.IsNullOrEmpty(id2))
                    id2 = MainUtil.GetID((object)XmlUtil.GetAttribute("bid", versionNode));
                DateTime dateTime = DateUtil.IsoDateToDateTime(XmlUtil.GetAttribute("created", versionNode), DateTime.MinValue);
                CoreItem.Builder builder = new CoreItem.Builder(target.ID, attribute1, id1, target.Database.DataManager);
                Language result;
                if (!Language.TryParse(attribute2, out result))
                    result = Language.Invariant;
                builder.SetLanguage(result);
                if (mode == VersionInstallMode.Append)
                {
                    if (removeOtherVersions)
                    {
                        str = Sitecore.Data.Version.First.ToString();
                    }
                    else
                    {
                        Sitecore.Data.Items.Item obj = target.Database.Items[target.ID, result];
                        if (obj != null)
                        {
                            Sitecore.Data.Version[] versionNumbers = obj.Versions.GetVersionNumbers(result);
                            str = versionNumbers == null || versionNumbers.Length == 0 ? Sitecore.Data.Version.First.ToString() : (versionNumbers[versionNumbers.Length - 1].Number + 1).ToString();
                        }
                        else
                            str = Sitecore.Data.Version.First.ToString();
                    }
                }
                builder.SetVersion(Sitecore.Data.Version.Parse(str));
                builder.SetBranchId(id2);
                builder.SetCreated(dateTime);
                foreach (XmlNode selectNode in versionNode.SelectNodes("fields/field"))
                    VersionInstaller.ParseField(selectNode, builder);
                return new Sitecore.Data.Items.Item(builder.ItemData.Definition.ID, builder.ItemData, target.Database);
            }

            /// <summary>Installs the version.</summary>
            /// <param name="version">The version.</param>
            private static void InstallVersion(Sitecore.Data.Items.Item version)
            {
                if (version == null)
                    return;
                version.Editing.BeginEdit();
                version.RuntimeSettings.ReadOnlyStatistics = true;
                version.RuntimeSettings.SaveAll = true;
                version.Editing.EndEdit(false, true);
                Sitecore.Data.Items.Item obj = version.Database.GetItem(version.Uri.ToDataUri());
                obj.Editing.BeginEdit();
                obj.Name = version.InnerData.Definition.Name;
                obj.TemplateID = version.InnerData.Definition.TemplateID;
                obj.BranchId = version.InnerData.Definition.BranchId;
                obj.RuntimeSettings.ReadOnlyStatistics = true;
                obj.Editing.EndEdit();
                string isoNowWithTicks = DateUtil.IsoNowWithTicks;
                bool flag1 = string.Compare(obj[FieldIDs.Created], isoNowWithTicks, StringComparison.OrdinalIgnoreCase) > 0;
                bool flag2 = string.Compare(obj[FieldIDs.Updated], isoNowWithTicks, StringComparison.OrdinalIgnoreCase) > 0;
                if (!(flag1 | flag2))
                    return;
                obj.Editing.BeginEdit();
                obj.RuntimeSettings.ReadOnlyStatistics = true;
                if (flag1)
                    obj[FieldIDs.Created] = isoNowWithTicks;
                if (flag2)
                    obj[FieldIDs.Updated] = isoNowWithTicks;
                obj.Editing.EndEdit();
            }

            /// <summary>Parses the field.</summary>
            /// <param name="node">The node.</param>
            /// <param name="builder">The builder.</param>
            private static void ParseField(XmlNode node, CoreItem.Builder builder)
            {
                ID fieldID = ID.Parse(XmlUtil.GetAttribute("tfid", node));
                XmlNode node1 = node.SelectSingleNode("content");
                if (node1 == null)
                    return;
                string fieldValue = XmlUtil.GetValue(node1);
                builder.AddField(fieldID, fieldValue);
            }

            /// <summary>
            /// Initiates the TemplateEngine.ChangeFieldSharing process.
            /// </summary>
            /// <param name="source">The source.</param>
            /// <param name="target">The target.</param>
            private static void UpdateFieldSharing(Sitecore.Data.Items.Item source, Sitecore.Data.Items.Item target)
            {
                if (source.TemplateID != TemplateIDs.TemplateField)
                    return;
                target = new ItemReference(target)
                {
                    Language = source.Language
                }.GetItem();
                target.Editing.BeginEdit();
                target.Fields[TemplateFieldIDs.Shared].Value = source.Fields[TemplateFieldIDs.Shared].Value;
                target.Fields[TemplateFieldIDs.Unversioned].Value = source.Fields[TemplateFieldIDs.Unversioned].Value;
                target.Editing.EndEdit();
            }
        }
    }
}