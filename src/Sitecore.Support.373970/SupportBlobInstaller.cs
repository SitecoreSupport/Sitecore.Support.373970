namespace Sitecore.Support.Install
{
    using Sitecore.Collections;
    using Sitecore.Configuration;
    using Sitecore.Data;
    using Sitecore.Data.Fields;
    using Sitecore.Data.Items;
    using Sitecore.Data.Managers;
    using Sitecore.Diagnostics;
    using Sitecore.Install.BlobData;
    using Sitecore.Install.Framework;
    using Sitecore.Install.Items;
    using Sitecore.Resources.Media;
    using Sitecore.SecurityModel.Cryptography;
    using System;
    using System.Collections.Generic;
    using System.IO;
    internal class SupportBlobInstaller : AdvancedBaseSink<PackageEntry, BlobInstallerContext>
    {
        private static readonly IHashEncryption HashEncryptionProvider = new HashEncryption();

        public SupportBlobInstaller(IProcessingContext context)
        {
            base.Initialize(context);
        }

        protected override BlobInstallerContext CreateContext()
        {
            return new BlobInstallerContext();
        }

        public static void FlushData(IProcessingContext processingContext)
        {
            IList<PackageEntry> entries = GetContext(processingContext).Entries;
            if (entries.Count != 0)
            {
                int num = 0;
                int num2 = 0;
                try
                {
                    foreach (PackageEntry entry in entries)
                    {
                        if (InstallEntry(entry, processingContext))
                        {
                            num++;
                            continue;
                        }
                        num2++;
                    }
                }
                finally
                {
                    BlobInstallerContext context = GetContext(processingContext);
                    context.Clear();
                }
                Log.Info(string.Format("Installing of blob values has been finished. Installed: {0} Skipped: {1}", num, num2), typeof(BlobInstaller));
            }
        }

        private static bool HasReference(Guid id, string databaseName, IProcessingContext context)
        {
            bool flag;
            using (IEnumerator<BlobLink> enumerator = GetContext(context).References.GetEnumerator())
            {
                while (true)
                {
                    if (enumerator.MoveNext())
                    {
                        BlobLink current = enumerator.Current;
                        if ((current.DatabaseName != databaseName) || !(current.Guid == id))
                        {
                            continue;
                        }
                        flag = true;
                    }
                    else
                    {
                        return false;
                    }
                    break;
                }
            }
            return flag;
        }

        private static bool InstallEntry(PackageEntry entry, IProcessingContext context)
        {
            char[] separator = new char[] { '/' };
            string[] strArray = entry.Key.Split(separator);
            if (strArray.Length != 3)
            {
                Log.Error(string.Format("Cannot parse entry key '{0}'", entry.Key), typeof(BlobInstaller));
                return false;
            }
            Guid guid = MainUtil.GetGuid(strArray[2]);
            if (guid == Guid.Empty)
            {
                Log.Error(string.Format("Cannot parse entry key '{0}'", entry.Key), typeof(BlobInstaller));
                return false;
            }
            if (strArray[1] == Sitecore.Install.Constants.MediaStreamsFolder)
            {
                return InstallFile(entry.GetStream(), guid, context);
            }
            string name = strArray[1];
            Database database = Factory.GetDatabase(name);
            if (database == null)
            {
                Log.Error(string.Format("Cannot find database with name '{0}'", name), typeof(BlobInstaller));
                return false;
            }
            if (!HasReference(guid, name, context))
            {
                return false;
            }
            ItemManager.SetBlobStream(entry.GetStream().Stream, guid, database);
            return true;
        }

        private static bool InstallFile(IStreamHolder stream, Guid guid, IProcessingContext context)
        {
            BlobInstallerContext context2 = GetContext(context);
            string str = null;
            for (int i = context2.FileReferences.Count - 1; i >= 0; i--)
            {
                FileReference reference = context2.FileReferences[i];
                if (MainUtil.GetMD5Hash(reference.FileName).Guid == guid)
                {
                    Item innerItem = reference.ItemReference.GetItem();
                    if (str != null)
                    {
                        innerItem.Editing.BeginEdit();
                        innerItem.Fields[reference.FieldID].Value = str;
                        innerItem.Editing.EndEdit();
                    }
                    else
                    {
                        MediaItem item = new MediaItem(innerItem);
                        Sitecore.Resources.Media.Media media = MediaManager.GetMedia(item);
                        media.SetStream(stream.Stream, item.Extension);
                        string filePath = media.MediaData.MediaItem.FilePath;
                        if (filePath != null)
                        {
                            str = filePath;
                        }
                    }
                    context2.FileReferences.RemoveAt(i);
                }
            }
            return (str != null);
        }

        public override void Put(PackageEntry entry)
        {
            base.Context.Entries.Add(entry);
        }

        private static bool StampsAreEqual(byte[] stamp1, byte[] stamp2)
        {
            if (stamp1.Length != stamp2.Length)
            {
                return false;
            }
            for (int i = 0; i < stamp1.Length; i++)
            {
                if (stamp1[i] != stamp2[i])
                {
                    return false;
                }
            }
            return true;
        }

        public static void UpdateBlobData(Item item, IProcessingContext context)
        {
            BlobInstallerContext context2 = GetContext(context);
            int num = 0;
            while (true)
            {
                while (true)
                {
                    if (num >= item.Fields.Count)
                    {
                        return;
                    }
                    Field field = item.Fields[num];
                    if (field != null)
                    {
                        //fix bug #373970
                        //original (field.Type == "text") replaced with (field.Type == "Single-Line Text")
                        if ((field.Type == "Single-Line Text") && ((field.Name.ToLowerInvariant() == "file path") && !string.IsNullOrEmpty(field.Value)))
                        //end of fix
                        {
                            ItemReference itemReference = new ItemReference(item);
                            context2.FileReferences.Add(new FileReference(itemReference, field.ID, field.Value));
                        }
                        if ((field.TypeKey == "attachment") && (field.HasValue && !string.IsNullOrEmpty(field.Value)))
                        {
                            string str = field.Value;
                            if (str.Length < 50)
                            {
                                Guid guid = MainUtil.GetGuid(str);
                                if (guid != Guid.Empty)
                                {
                                    context2.References.Add(new BlobLink(item.Database.Name, guid));
                                    break;
                                }
                            }
                            Guid empty = Guid.Empty;
                            byte[] dataBytes = System.Convert.FromBase64String(str);
                            byte[] buffer2 = HashEncryptionProvider.ComputeHash(dataBytes);
                            foreach (Pair<byte[], Guid> pair in context2.ImageCache)
                            {
                                if (StampsAreEqual(buffer2, pair.Part1))
                                {
                                    empty = pair.Part2;
                                    break;
                                }
                            }
                            if (empty == Guid.Empty)
                            {
                                empty = Guid.NewGuid();
                                ItemManager.SetBlobStream(new MemoryStream(dataBytes), empty, item.Database);
                                context2.ImageCache.Add(new Pair<byte[], Guid>(buffer2, empty));
                            }
                            item.Editing.BeginEdit();
                            item.RuntimeSettings.ReadOnlyStatistics = true;
                            item.RuntimeSettings.SaveAll = true;
                            field.Value = empty.ToString();
                            item.Editing.EndEdit();
                        }
                    }
                    break;
                }
                num++;
            }
        }
    }
}