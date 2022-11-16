using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Nova
{
    public class CheckpointManager : MonoBehaviour
    {
        public string saveFolder;
        private string savePathBase;
        private string globalSavePath;

        private GlobalSave globalSave;
        private bool globalSaveDirty;

        // nodeName => dialogueIndex => data
        private readonly Dictionary<string, List<ReachedDialogueData>> reachedDialogues =
            new Dictionary<string, List<ReachedDialogueData>>();

        private readonly SerializableHashSet<string> reachedEnds = new SerializableHashSet<string>();

        private readonly Dictionary<int, Bookmark> cachedSaveSlots = new Dictionary<int, Bookmark>();
        public readonly Dictionary<int, BookmarkMetadata> saveSlotsMetadata = new Dictionary<int, BookmarkMetadata>();

        private CheckpointSerializer serializer;

        private bool inited;

        private void InitGlobalSave()
        {
            globalSave = serializer.DeserializeRecord<GlobalSave>(CheckpointSerializer.GlobalSaveOffset);
        }

        private void InitReached()
        {
            reachedDialogues.Clear();
            reachedEnds.Clear();
            for (var cur = globalSave.beginReached; cur < globalSave.endReached; cur = serializer.NextRecord(cur))
            {
                var record = serializer.DeserializeRecord<IReachedData>(cur);
                if (record is ReachedEndData end)
                {
                    reachedEnds.Add(end.endName);
                }
                else if (record is ReachedDialogueData dialogue)
                {
                    SetReachedDialogueData(dialogue);
                }
            }

            // check each reached data is a prefix
            foreach (var reachedList in reachedDialogues)
            {
                if (reachedList.Value.Contains(null))
                {
                    throw CheckpointCorruptedException.BadReachedData(reachedList.Key);
                }
            }
        }

        // This now needs to be called in GameState.Awake, so consider removing calls in Start
        public void Init()
        {
            if (inited)
            {
                return;
            }

            savePathBase = Path.Combine(Application.persistentDataPath, "Save", saveFolder);
            globalSavePath = Path.Combine(savePathBase, "global.nsav");
            Directory.CreateDirectory(savePathBase);

            serializer = new CheckpointSerializer(globalSavePath);
            if (!File.Exists(globalSavePath))
            {
                ResetGlobalSave();
            }
            else
            {
                serializer.Open();
                InitGlobalSave();
                InitReached();
            }

            foreach (string fileName in Directory.GetFiles(savePathBase, "sav*.nsav*"))
            {
                var result = Regex.Match(fileName, @"sav([0-9]+)\.nsav");
                if (result.Groups.Count > 1 && int.TryParse(result.Groups[1].Value, out int id))
                {
                    saveSlotsMetadata.Add(id, new BookmarkMetadata
                    {
                        saveID = id,
                        modifiedTime = File.GetLastWriteTime(fileName)
                    });
                }
            }

            // Debug.Log("Nova: CheckpointManager initialized.");
            inited = true;
        }

        private void Start()
        {
            Init();
        }

        private void OnDestroy()
        {
            UpdateGlobalSave();

            foreach (var bookmark in cachedSaveSlots.Values)
            {
                bookmark.DestroyTexture();
            }

            serializer.Dispose();
        }

        #region Global save

        private void NewReached()
        {
            globalSave.endReached = serializer.NextRecord(globalSave.endReached);
            globalSaveDirty = true;
            // Debug.Log($"next reached {globalSave.endReached}");
        }

        private void NewCheckpoint()
        {
            globalSave.endCheckpoint = serializer.NextRecord(globalSave.endCheckpoint);
            globalSaveDirty = true;
            // Debug.Log($"next checkpoint {globalSave.endCheckpoint}");
        }

        public long NextRecord(long offset)
        {
            return serializer.NextRecord(offset);
        }

        public long NextCheckpoint(long offset)
        {
            return NextRecord(NextRecord(offset));
        }

        public NodeRecord GetNextNode(NodeRecord prevRecord, string name, Variables variables, int beginDialogue)
        {
            var variablesHash = variables.hash;
            NodeRecord record = null;
            var offset = prevRecord?.child ?? globalSave.beginCheckpoint;
            while (offset != 0 && offset < globalSave.endCheckpoint)
            {
                record = serializer.GetNodeRecord(offset);
                if (record.name == name && record.variablesHash == variablesHash)
                {
                    return record;
                }

                offset = record.sibling;
            }

            offset = globalSave.endCheckpoint;
            var newRecord = new NodeRecord(offset, name, beginDialogue, variablesHash);
            if (record != null)
            {
                record.sibling = offset;
                serializer.UpdateNodeRecord(record);
            }
            else if (prevRecord != null)
            {
                prevRecord.child = offset;
                serializer.UpdateNodeRecord(prevRecord);
            }

            if (prevRecord != null)
            {
                newRecord.parent = prevRecord.offset;
            }

            serializer.UpdateNodeRecord(newRecord);
            NewCheckpoint();
            return newRecord;
        }

        public NodeRecord GetNodeRecord(long offset)
        {
            return serializer.GetNodeRecord(offset);
        }

        public bool CanAppendCheckpoint(long checkpointOffset)
        {
            return NextRecord(checkpointOffset) >= globalSave.endCheckpoint ||
                   NextCheckpoint(checkpointOffset) >= globalSave.endCheckpoint;
        }

        public void AppendDialogue(NodeRecord nodeRecord, int dialogueIndex, bool shouldSaveCheckpoint)
        {
            nodeRecord.endDialogue = dialogueIndex + 1;
            if (shouldSaveCheckpoint)
            {
                nodeRecord.lastCheckpointDialogue = dialogueIndex;
            }

            serializer.UpdateNodeRecord(nodeRecord);
        }

        public long AppendCheckpoint(int dialogueIndex, GameStateCheckpoint checkpoint)
        {
            var record = globalSave.endCheckpoint;

            var buf = new ByteSegment(new byte[4]);
            buf.WriteInt(0, dialogueIndex);
            serializer.AppendRecord(record, buf);
            NewCheckpoint();

            serializer.SerializeRecord(globalSave.endCheckpoint, checkpoint);
            NewCheckpoint();
            return record;
        }

        public int GetCheckpointDialogue(long offset)
        {
            return serializer.GetRecord(offset).ReadInt(0);
        }

        public GameStateCheckpoint GetCheckpoint(long offset)
        {
            return serializer.DeserializeRecord<GameStateCheckpoint>(serializer.NextRecord(offset));
        }

        private void SetReachedDialogueData(ReachedDialogueData data)
        {
            var list = reachedDialogues.Ensure(data.nodeName);
            list.Ensure(data.dialogueIndex + 1);
            list[data.dialogueIndex] = data;
        }

        public void SetReached(ReachedDialogueData data)
        {
            if (IsReachedAnyHistory(data.nodeName, data.dialogueIndex))
            {
                return;
            }
            SetReachedDialogueData(data);
            serializer.SerializeRecord<IReachedData>(globalSave.endReached, data);
            NewReached();
        }

        public void SetBranchReached(NodeRecord nodeRecord, string branchName)
        {
            // currently we cannot find next node by branchName
            throw new NotImplementedException();
        }

        public void SetEndReached(string endName)
        {
            if (reachedEnds.Contains(endName))
            {
                return;
            }

            reachedEnds.Add(endName);
            var reachedData = (IReachedData)new ReachedEndData(endName);
            serializer.SerializeRecord(globalSave.endReached, reachedData);
            NewReached();
        }

        public bool IsReachedAnyHistory(string nodeName, int dialogueIndex)
        {
            return reachedDialogues.ContainsKey(nodeName) &&
                dialogueIndex < reachedDialogues[nodeName].Count;
        }

        public ReachedDialogueData GetReachedDialogueData(string nodeName, int dialogueIndex)
        {
            return reachedDialogues[nodeName][dialogueIndex];
        }

        public bool IsBranchReached(NodeRecord nodeRecord, string nextNodeName)
        {
            throw new NotImplementedException();
        }

        public bool IsBranchReachedAnyHistory(string nodeName, string nextNodeName)
        {
            // currently we don't store this
            throw new NotImplementedException();
        }

        public bool IsEndReached(string endName)
        {
            return reachedEnds.Contains(endName);
        }

        public void CheckScript(ScriptLoader scriptLoader, FlowChartTree flowChart)
        {
            if (globalSave.nodeHashes != null)
            {
                // TODO: upgrade global save
                foreach (var node in flowChart)
                {
                    if (globalSave.nodeHashes.ContainsKey(node.name) && globalSave.nodeHashes[node.name] != node.textHash &&
                        reachedDialogues.ContainsKey(node.name))
                    {
                        Debug.Log($"node need upgrade {node.name}");

                        scriptLoader.AddDeferredDialogueChunks(node);
                        Differ differ = new Differ(node, reachedDialogues[node.name]);
                        differ.GetDiffs(out var deletes, out var inserts);

                        Debug.Log($"diff: deletes={deletes.Dump()}, inserts={inserts.Dump()}");

                        globalSaveDirty = true;
                    }
                }
            }
            globalSave.nodeHashes = flowChart.ToDictionary(node => node.name, node => node.textHash);

        }

        public void UpdateGlobalSave()
        {
            if (globalSaveDirty)
            {
                serializer.SerializeRecord(CheckpointSerializer.GlobalSaveOffset, globalSave);
                globalSaveDirty = false;
            }

            serializer.Flush();
        }

        /// <summary>
        /// Reset the global save file to clear all progress.
        /// Note that all bookmarks will be invalid.
        /// </summary>
        private void ResetGlobalSave()
        {
            var saveDir = new DirectoryInfo(savePathBase);
            foreach (var file in saveDir.GetFiles())
            {
                file.Delete();
            }

            serializer.Open();
            globalSave = new GlobalSave(serializer);
            globalSaveDirty = true;
            InitReached();
        }

        #endregion

        #region Bookmarks

        private string GetBookmarkFileName(int saveID)
        {
            return Path.Combine(savePathBase, $"sav{saveID:D3}.nsav");
        }

        private Bookmark ReplaceCache(int saveID, Bookmark bookmark)
        {
            if (cachedSaveSlots.ContainsKey(saveID))
            {
                var old = cachedSaveSlots[saveID];
                if (old == bookmark)
                {
                    return bookmark;
                }

                Destroy(old.screenshot);
            }

            if (bookmark == null)
            {
                cachedSaveSlots.Remove(saveID);
            }
            else
            {
                cachedSaveSlots[saveID] = bookmark;
            }

            return bookmark;
        }

        /// <summary>
        /// Save a bookmark to disk, and update the global save file.
        /// Will throw exception if it fails.
        /// </summary>
        /// <param name="saveID">ID of the bookmark.</param>
        /// <param name="bookmark">The bookmark to save.</param>
        public void SaveBookmark(int saveID, Bookmark bookmark)
        {
            var screenshot = new Texture2D(bookmark.screenshot.width, bookmark.screenshot.height,
                bookmark.screenshot.format, false);
            screenshot.SetPixels32(bookmark.screenshot.GetPixels32());
            screenshot.Apply();
            bookmark.screenshot = screenshot;
            bookmark.globalSaveIdentifier = globalSave.identifier;

            serializer.WriteBookmark(GetBookmarkFileName(saveID), ReplaceCache(saveID, bookmark));
            UpdateGlobalSave();

            var metadata = saveSlotsMetadata.Ensure(saveID);
            metadata.saveID = saveID;
            metadata.modifiedTime = DateTime.Now;
        }

        /// <summary>
        /// Load a bookmark from disk. Never uses cache.
        /// Will throw exception if it fails.
        /// </summary>
        /// <param name="saveID">ID of the bookmark.</param>
        /// <returns>The loaded bookmark.</returns>
        public Bookmark LoadBookmark(int saveID)
        {
            var bookmark = serializer.ReadBookmark(GetBookmarkFileName(saveID));
            if (bookmark.globalSaveIdentifier != globalSave.identifier)
            {
                Debug.LogWarning($"Nova: Save file is incompatible with the global save file. saveID: {saveID}");
                bookmark = null;
            }

            return ReplaceCache(saveID, bookmark);
        }

        /// <summary>
        /// Delete a specified bookmark.
        /// </summary>
        /// <param name="saveID">ID of the bookmark.</param>
        public void DeleteBookmark(int saveID)
        {
            File.Delete(GetBookmarkFileName(saveID));
            saveSlotsMetadata.Remove(saveID);
            ReplaceCache(saveID, null);
        }

        /// <summary>
        /// Load the contents of all existing bookmark in the given range eagerly.
        /// </summary>
        /// <param name="beginSaveID">The beginning of the range, inclusive.</param>
        /// <param name="endSaveID">The end of the range, exclusive.</param>
        public void EagerLoadRange(int beginSaveID, int endSaveID)
        {
            for (; beginSaveID < endSaveID; beginSaveID++)
            {
                if (saveSlotsMetadata.ContainsKey(beginSaveID))
                    LoadBookmark(beginSaveID);
            }
        }

        /// <summary>
        /// Load / Save a bookmark by ID. Will use cached result if exists.
        /// </summary>
        /// <param name="saveID">ID of the bookmark.</param>
        /// <returns>The cached or loaded bookmark</returns>
        public Bookmark this[int saveID]
        {
            get
            {
                if (!saveSlotsMetadata.ContainsKey(saveID))
                    return null;
                if (!cachedSaveSlots.ContainsKey(saveID))
                    LoadBookmark(saveID);
                return cachedSaveSlots[saveID];
            }

            set => SaveBookmark(saveID, value);
        }

        /// <summary>
        /// Query the ID of the latest / earliest bookmark.
        /// </summary>
        /// <param name="begin">Beginning ID of the query range, inclusive.</param>
        /// <param name="end">Ending ID of the query range, exclusive.</param>
        /// <param name="type">Type of this query.</param>
        /// <returns>The ID to query. If no bookmark is found in range, the return value will be "begin".</returns>
        public int QuerySaveIDByTime(int begin, int end, SaveIDQueryType type)
        {
            var filtered = saveSlotsMetadata.Values.Where(m => m.saveID >= begin && m.saveID < end).ToList();
            if (!filtered.Any())
                return begin;
            if (type == SaveIDQueryType.Earliest)
                return filtered.Aggregate((agg, val) => agg.modifiedTime < val.modifiedTime ? agg : val).saveID;
            else
                return filtered.Aggregate((agg, val) => agg.modifiedTime > val.modifiedTime ? agg : val).saveID;
        }

        public int QueryMaxSaveID(int begin)
        {
            if (!saveSlotsMetadata.Any())
            {
                return begin;
            }

            return Math.Max(saveSlotsMetadata.Keys.Max(), begin);
        }

        public int QueryMinUnusedSaveID(int begin, int end = int.MaxValue)
        {
            int saveID = begin;
            while (saveID < end && saveSlotsMetadata.ContainsKey(saveID))
            {
                ++saveID;
            }

            return saveID;
        }

        #endregion

        #region Auxiliary data

        /// <summary>
        /// Get global data
        /// </summary>
        /// <see cref="GlobalSave"/>
        /// <param name="key">the key of the data</param>
        /// <param name="defaultValue">the default value if the key is not found</param>
        /// <typeparam name="T">the type of the data</typeparam>
        /// <returns>the stored data</returns>
        public T Get<T>(string key, T defaultValue = default)
        {
            if (globalSave.data.TryGetValue(key, out var value))
            {
                return (T)Convert.ChangeType(value, typeof(T));
            }
            else
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// Set global data
        /// </summary>
        /// <see cref="GlobalSave"/>
        /// <param name="key">the key of the data</param>
        /// <param name="value">the data to store</param>
        public void Set(string key, object value)
        {
            globalSave.data[key] = value;
            globalSaveDirty = true;
        }

        #endregion
    }
}
