/*
 * NOVALYTH R2
 * Remote Hypergrid suitcase inventory policy wrapper.
 *
 * The public HG inventory endpoint keeps the existing suitcase restrictions,
 * while all inventory storage operations are delegated to the dedicated
 * Inventory Core through XInventoryServicesConnector.
 */

using System;
using System.Collections.Generic;
using System.Reflection;

using log4net;
using Nini.Config;
using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;

namespace OpenSim.Services.Connectors
{
    public class RemoteHGSuitcaseInventoryService : IInventoryService
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly XInventoryServicesConnector m_BackingService;
        private readonly IAvatarService m_AvatarService;

        private readonly ExpiringCache<UUID, AvatarAppearance> m_Appearances = new();

        private readonly string m_ConfigName;

        public RemoteHGSuitcaseInventoryService(IConfigSource config)
            : this(config, "HGInventoryService")
        {
        }

        public RemoteHGSuitcaseInventoryService(IConfigSource config, string configName)
        {
            m_ConfigName = string.IsNullOrEmpty(configName)
                ? "HGInventoryService"
                : configName;

            IConfig invConfig = config.Configs[m_ConfigName]
                ?? throw new Exception($"Missing [{m_ConfigName}] configuration");

            string inventoryServerUri =
                invConfig.GetString("InventoryServerURI", string.Empty);

            if (string.IsNullOrWhiteSpace(inventoryServerUri))
                throw new Exception(
                    $"Please specify InventoryServerURI in [{m_ConfigName}]");

            m_BackingService = new XInventoryServicesConnector(config, m_ConfigName);

            string avatarDll =
                invConfig.GetString("AvatarService", string.Empty);

            if (string.IsNullOrWhiteSpace(avatarDll))
                throw new Exception(
                    $"Please specify AvatarService in [{m_ConfigName}]");

            object[] args = [ config ];

            m_AvatarService =
                ServerUtils.LoadPlugin<IAvatarService>(avatarDll, args)
                ?? throw new Exception(
                    $"Unable to create AvatarService from {avatarDll}");

            m_log.InfoFormat(
                "[NOVALYTH HG INVENTORY]: Remote suitcase backing ready: {0}",
                inventoryServerUri);
        }

        public bool CreateUserInventory(UUID principalID)
        {
            return false;
        }

        public List<InventoryFolderBase> GetInventorySkeleton(UUID principalID)
        {
            List<InventoryFolderBase> all = GetAllFolders(principalID);
            InventoryFolderBase suitcase = FindSuitcase(all);

            if (suitcase == null)
            {
                m_log.WarnFormat(
                    "[NOVALYTH HG INVENTORY]: No suitcase folder for {0}",
                    principalID);
                return null;
            }

            List<InventoryFolderBase> result = [];

            foreach (InventoryFolderBase folder in all)
            {
                if (folder.ID == suitcase.ID)
                    continue;

                if (IsDescendantOf(folder.ID, suitcase.ID, all))
                    result.Add(folder);
            }

            result.Add(suitcase);
            return result;
        }

        public InventoryFolderBase GetRootFolder(UUID principalID)
        {
            InventoryFolderBase root =
                m_BackingService.GetRootFolder(principalID);

            if (root == null)
                return null;

            List<InventoryFolderBase> all = GetAllFolders(principalID);
            InventoryFolderBase suitcase = FindSuitcase(all);

            if (suitcase == null)
            {
                InventoryFolderBase legacy = all.Find(
                    f => f.Name == InventoryFolderBase.SUITCASE_FOLDER_NAME
                        && f.ParentID == UUID.Zero);

                if (legacy != null)
                {
                    legacy.ParentID = root.ID;
                    legacy.Type = (short)FolderType.Suitcase;

                    if (!m_BackingService.UpdateFolder(legacy))
                    {
                        m_log.ErrorFormat(
                            "[NOVALYTH HG INVENTORY]: Failed to migrate legacy suitcase for {0}",
                            principalID);
                        return null;
                    }

                    suitcase = legacy;
                }
            }

            if (suitcase == null)
            {
                suitcase = new InventoryFolderBase(
                    UUID.Random(),
                    InventoryFolderBase.SUITCASE_FOLDER_NAME,
                    principalID,
                    (short)FolderType.Suitcase,
                    root.ID,
                    1);

                if (!m_BackingService.AddFolder(suitcase))
                {
                    m_log.ErrorFormat(
                        "[NOVALYTH HG INVENTORY]: Failed to create suitcase for {0}",
                        principalID);
                    return null;
                }

                CreateSystemFolders(principalID, suitcase.ID);
            }

            return suitcase;
        }

        public InventoryFolderBase GetFolderForType(
            UUID principalID, FolderType type)
        {
            List<InventoryFolderBase> all = GetAllFolders(principalID);
            InventoryFolderBase suitcase = FindSuitcase(all);

            if (suitcase == null)
                return null;

            return all.Find(
                f => f.ParentID == suitcase.ID
                    && f.Type == (short)type);
        }

        public InventoryCollection GetFolderContent(
            UUID principalID, UUID folderID)
        {
            if (!IsWithinSuitcaseTree(principalID, folderID))
                return new InventoryCollection();

            return m_BackingService.GetFolderContent(principalID, folderID)
                ?? new InventoryCollection();
        }

        public InventoryCollection[] GetMultipleFoldersContent(
            UUID principalID, UUID[] folderIDs)
        {
            // Preserve the existing HGSuitcaseInventoryService behavior:
            // this method was inherited directly from XInventoryService.
            return m_BackingService.GetMultipleFoldersContent(
                principalID, folderIDs);
        }

        public List<InventoryItemBase> GetFolderItems(
            UUID principalID, UUID folderID)
        {
            if (!IsWithinSuitcaseTree(principalID, folderID))
                return [];

            return m_BackingService.GetFolderItems(principalID, folderID)
                ?? [];
        }

        public bool AddFolder(InventoryFolderBase folder)
        {
            if (!IsWithinSuitcaseTree(folder.Owner, folder.ParentID))
                return false;

            return m_BackingService.AddFolder(folder);
        }

        public bool UpdateFolder(InventoryFolderBase folder)
        {
            if (!IsWithinSuitcaseTree(folder.Owner, folder.ID))
                return false;

            return m_BackingService.UpdateFolder(folder);
        }

        public bool MoveFolder(InventoryFolderBase folder)
        {
            if (!IsWithinSuitcaseTree(folder.Owner, folder.ID))
                return false;

            if (!IsWithinSuitcaseTree(folder.Owner, folder.ParentID))
                return false;

            return m_BackingService.MoveFolder(folder);
        }

        public bool DeleteFolders(UUID principalID, List<UUID> folderIDs)
        {
            return false;
        }

        public bool PurgeFolder(InventoryFolderBase folder)
        {
            return false;
        }

        public bool AddItem(InventoryItemBase item)
        {
            if (!IsWithinSuitcaseTree(item.Owner, item.Folder))
                return false;

            return m_BackingService.AddItem(item);
        }

        public bool UpdateItem(InventoryItemBase item)
        {
            if (!IsWithinSuitcaseTree(item.Owner, item.Folder))
                return false;

            return m_BackingService.UpdateItem(item);
        }

        public bool MoveItems(UUID principalID, List<InventoryItemBase> items)
        {
            if (items == null || items.Count == 0)
                return false;

            foreach (InventoryItemBase item in items)
            {
                if (!IsWithinSuitcaseTree(item.Owner, item.Folder))
                    return false;
            }

            foreach (InventoryItemBase item in items)
            {
                InventoryItemBase original =
                    m_BackingService.GetItem(item.Owner, item.ID);

                if (original == null)
                    return false;

                if (!IsWithinSuitcaseTree(original.Owner, original.Folder))
                    return false;
            }

            return m_BackingService.MoveItems(principalID, items);
        }

        public bool DeleteItems(UUID principalID, List<UUID> itemIDs)
        {
            return false;
        }

        public InventoryItemBase GetItem(UUID principalID, UUID itemID)
        {
            InventoryItemBase item =
                m_BackingService.GetItem(principalID, itemID);

            if (item == null)
                return null;

            if (!IsWithinSuitcaseTree(item.Owner, item.Folder)
                && !IsPartOfAppearance(item.Owner, item.ID))
            {
                return null;
            }

            return item;
        }

        public InventoryItemBase[] GetMultipleItems(
            UUID principalID, UUID[] ids)
        {
            // Preserve existing inherited behavior for compatibility.
            return m_BackingService.GetMultipleItems(principalID, ids);
        }

        public InventoryFolderBase GetFolder(
            UUID principalID, UUID folderID)
        {
            InventoryFolderBase folder =
                m_BackingService.GetFolder(principalID, folderID);

            if (folder == null)
                return null;

            if (!IsWithinSuitcaseTree(folder.Owner, folder.ID))
                return null;

            return folder;
        }

        public bool HasInventoryForUser(UUID principalID)
        {
            return m_BackingService.HasInventoryForUser(principalID);
        }

        public List<InventoryItemBase> GetActiveGestures(UUID principalID)
        {
            return m_BackingService.GetActiveGestures(principalID);
        }

        public int GetAssetPermissions(UUID principalID, UUID assetID)
        {
            return m_BackingService.GetAssetPermissions(
                principalID, assetID);
        }

        private List<InventoryFolderBase> GetAllFolders(UUID principalID)
        {
            return m_BackingService.GetInventorySkeleton(principalID)
                ?? [];
        }

        private static InventoryFolderBase FindSuitcase(
            List<InventoryFolderBase> folders)
        {
            return folders.Find(
                f => f.Type == (short)FolderType.Suitcase);
        }

        private static bool IsDescendantOf(
            UUID folderID,
            UUID ancestorID,
            List<InventoryFolderBase> all)
        {
            if (folderID == ancestorID)
                return true;

            Dictionary<UUID, InventoryFolderBase> byId = new();

            foreach (InventoryFolderBase folder in all)
                byId[folder.ID] = folder;

            UUID current = folderID;

            for (int i = 0; i <= all.Count + 1; ++i)
            {
                if (current == ancestorID)
                    return true;

                if (!byId.TryGetValue(current, out InventoryFolderBase folder))
                    return false;

                if (folder.ParentID == current)
                    return false;

                current = folder.ParentID;
            }

            return false;
        }

        private bool IsWithinSuitcaseTree(UUID principalID, UUID folderID)
        {
            List<InventoryFolderBase> all = GetAllFolders(principalID);
            InventoryFolderBase suitcase = FindSuitcase(all);

            if (suitcase == null)
                return false;

            if (folderID == suitcase.ID)
                return true;

            if (IsDescendantOf(folderID, suitcase.ID, all))
                return true;

            InventoryFolderBase root =
                m_BackingService.GetRootFolder(principalID);

            if (root == null)
                return false;

            InventoryFolderBase currentOutfit = all.Find(
                f => f.ParentID == root.ID
                    && f.Type == (short)FolderType.CurrentOutfit);

            return currentOutfit != null
                && currentOutfit.ID == folderID;
        }

        private void CreateSystemFolders(UUID principalID, UUID suitcaseID)
        {
            InventoryCollection content =
                m_BackingService.GetFolderContent(principalID, suitcaseID);

            List<InventoryFolderBase> existing =
                content?.Folders ?? [];

            (FolderType Type, string Name)[] required =
            [
                (FolderType.Animation, "Animations"),
                (FolderType.BodyPart, "Body Parts"),
                (FolderType.CallingCard, "Calling Cards"),
                (FolderType.Clothing, "Clothing"),
                (FolderType.CurrentOutfit, "Current Outfit"),
                (FolderType.Favorites, "Favorites"),
                (FolderType.Gesture, "Gestures"),
                (FolderType.Landmark, "Landmarks"),
                (FolderType.LostAndFound, "Lost And Found"),
                (FolderType.Notecard, "Notecards"),
                (FolderType.Object, "Objects"),
                (FolderType.Snapshot, "Photo Album"),
                (FolderType.LSLText, "Scripts"),
                (FolderType.Sound, "Sounds"),
                (FolderType.Texture, "Textures"),
                (FolderType.Trash, "Trash"),
                (FolderType.Settings, "Settings")
            ];

            foreach ((FolderType type, string name) in required)
            {
                if (existing.Exists(f => f.Type == (short)type))
                    continue;

                InventoryFolderBase folder = new(
                    UUID.Random(),
                    name,
                    principalID,
                    (short)type,
                    suitcaseID,
                    1);

                if (!m_BackingService.AddFolder(folder))
                {
                    m_log.WarnFormat(
                        "[NOVALYTH HG INVENTORY]: Failed to create suitcase system folder {0} for {1}",
                        name,
                        principalID);
                }
            }
        }

        private AvatarAppearance GetAppearance(UUID principalID)
        {
            if (m_Appearances.TryGetValue(
                principalID, out AvatarAppearance appearance))
            {
                return appearance;
            }

            appearance = m_AvatarService.GetAppearance(principalID);

            if (appearance != null)
                m_Appearances.AddOrUpdate(principalID, appearance, 5 * 60);

            return appearance;
        }

        private bool IsPartOfAppearance(UUID principalID, UUID itemID)
        {
            AvatarAppearance appearance = GetAppearance(principalID);

            if (appearance == null)
                return false;

            for (int i = 0; i < appearance.Wearables.Length; ++i)
            {
                for (int j = 0; j < appearance.Wearables[i].Count; ++j)
                {
                    if (appearance.Wearables[i][j].ItemID == itemID)
                        return true;
                }
            }

            return appearance.GetAttachmentForItem(itemID) != null;
        }
    }
}
