﻿using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using DotCMIS.Client;
using DotCMIS;
using DotCMIS.Client.Impl;
using DotCMIS.Exceptions;
using DotCMIS.Enums;
using System.ComponentModel;
using System.Collections;
using DotCMIS.Data.Impl;

using System.Net;
using CmisSync.Lib;
using DotCMIS.Data;
using CmisSync.Lib.Cmis;

namespace CmisSync.Lib.Sync
{
    public partial class CmisRepo : RepoBase
    {
        /// <summary>
        /// Synchronization with a particular CMIS folder.
        /// </summary>
        public partial class SynchronizedFolder
        {

            /// <summary>
            /// Synchronize using the ChangeLog feature of CMIS to trigger CrawlStrategy.
            /// </summary>
            private void ChangeLogThenCrawlSync(IFolder remoteFolder, string localFolder)
            {
                // Get last change log token on server side.
                string lastTokenOnServer = CmisUtils.GetChangeLogToken(session);

                // Get last change token that had been saved on client side.
                // TODO catch exception invalidArgument which means that changelog has been truncated and this token is not found anymore.
                string lastTokenOnClient = database.GetChangeLogToken();

                if (lastTokenOnClient == lastTokenOnServer)
                {
                    Logger.Debug("No changes to sync, tokens on server and client are equal: " + lastTokenOnClient);
                    return;
                }

                if (lastTokenOnClient == null)
                {
                    // Token is null, which means no sync has ever happened yet, so just sync everything from remote.
                    CrawlRemote(remoteFolder, repoinfo.TargetDirectory, new List<string>(), new List<string>());
                    
                    Logger.Info("Synced from remote, updating ChangeLog token: " + lastTokenOnServer);
                    database.SetChangeLogToken(lastTokenOnServer);
                }

                do
                {
                    // Calculate queryable number of changes.
                    Config.Feature features = null;
                    if(ConfigManager.CurrentConfig.getFolder(repoinfo.Name)!=null)
                        features = ConfigManager.CurrentConfig.getFolder(repoinfo.Name).SupportedFeatures;
                    int maxNumItems = (features!=null && features.MaxNumberOfContentChanges!=null)?
                        (int)features.MaxNumberOfContentChanges : 100;

                    // Check which documents/folders have changed.
                    IChangeEvents changes = session.GetContentChanges(lastTokenOnClient, IsPropertyChangesSupported, maxNumItems);

                    // Apply changes.
                    foreach (IChangeEvent change in changes.ChangeEventList)
                    {
                        // Check whether change is applicable.
                        if (ChangeIsApplicable(change))
                        {
                            // Launch a CrawlSync (which means syncing everything indistinctively).
                            CrawlSync(remoteFolder, localFolder);

                            // A single CrawlSync takes care of all pending changes, so no need to analyze the rest of the changes.
                            return;
                        }
                    }
                }
                while (!lastTokenOnServer.Equals(lastTokenOnClient));
            }


            /// <summary>
            /// Apply a remote change for Created or Updated.
            /// <returns>Whether the change was applied successfully</returns>
            /// </summary>
            private bool ChangeIsApplicable(IChangeEvent change)
            {
                ICmisObject cmisObject = null;
                IFolder remoteFolder = null;
                IDocument remoteDocument = null;
                string remotePath = null;
                IFolder remoteParent = null;
                var changeIdForDebug = change.Properties.ContainsKey("cmis:name") ?
                    change.Properties["cmis:name"][0] : change.Properties["cmis:objectId"][0];

                // Get the remote changed object.
                try
                {
                    cmisObject = session.GetObject(change.ObjectId);
                }
                catch(CmisObjectNotFoundException)
                {
                    Logger.Info("Removed object, syncing might be needed:" + changeIdForDebug);
                    return true;
                }
                catch (Exception e)
                {
                    Logger.Warn("An exception occurred:" + change.ObjectId + " :", e);
                    return true; // Better be on the safe side and sync.
                }

                // Check whether change is about a document or folder.
                remoteDocument = cmisObject as IDocument;
                remoteFolder = cmisObject as IFolder;
                if (remoteDocument == null && remoteFolder == null)
                {
                    Logger.Info("Ignore change as it is not about a document nor folder: " + changeIdForDebug);
                    return false;
                }

                // Check whether it is a document worth syncing.
                if (remoteDocument != null)
                {
                    if ( ! Utils.IsFileWorthSyncing(remoteDocument.Name, repoinfo))
                    {
                        Logger.Info("Ignore change as it is about a document unworth syncing: " + changeIdForDebug);
                        return false;
                    }
                    if (remoteDocument.Paths.Count == 0)
                    {
                        Logger.Info("Ignore the unfiled object: " + changeIdForDebug);
                        return false;
                    }
                    // TODO: Support Multiple Paths
                    remotePath = remoteDocument.Paths[0];
                    remoteParent = remoteDocument.Parents[0];
                }

                // Check whether it is a folder worth syncing.
                if (remoteFolder != null)
                {
                    remotePath = remoteFolder.Path;
                    remoteParent = remoteFolder.FolderParent;
                    foreach (string name in remotePath.Split('/'))
                    {
                        if ( ! String.IsNullOrEmpty(name) && Utils.IsInvalidFolderName(name))
                        {
                            Logger.Info(String.Format("Ignore change as it is in a path unworth syncing:  {0}: {1}", name, remotePath));
                            return false;
                        }
                    }
                }

                // Ignore the change if not in a synchronized folder.
                if ( ! remotePath.StartsWith(this.remoteFolderPath))
                {
                    Logger.Info("Ignore change as it is not in the synchronized folder's path: " + remotePath);
                    return false;
                }
                if (this.repoinfo.isPathIgnored(remotePath))
                {
                    Logger.Info("Ignore change as it is in a path configured to be ignored: " + remotePath);
                    return false;
                }
                string relativePath = remotePath.Substring(remoteFolderPath.Length);
                if (relativePath.Length <= 0)
                {
                    Logger.Info("Ignore change as it is above the synchronized folder's path:: " + remotePath);
                    return false;
                }

                Logger.Debug("Change is applicable:" + changeIdForDebug);
                return true;
            }
        }
    }
}