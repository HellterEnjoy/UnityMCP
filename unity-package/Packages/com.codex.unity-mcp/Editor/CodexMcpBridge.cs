using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

#pragma warning disable 0618

namespace CodexUnityMcp
{
    [InitializeOnLoad]
    public static class CodexMcpBridge
    {
        private const int DefaultPort = 8765;
        private const string PrefixHost = "127.0.0.1";
        private const string GitPackageUrl = "https://github.com/HellterEnjoy/UnityMCP.git?path=/unity-package/Packages/com.codex.unity-mcp#main";
        private static readonly ConcurrentQueue<BridgeRequest> Requests = new ConcurrentQueue<BridgeRequest>();

        private static HttpListener _listener;
        private static Thread _listenerThread;
        private static volatile bool _running;
        private static int _port;
        private static AddRequest _packageUpdateRequest;

        static CodexMcpBridge()
        {
            _port = EditorPrefs.GetInt("CodexMcpBridge.Port", DefaultPort);
            EditorApplication.update += ProcessRequests;
            EditorApplication.delayCall += StartServer;
        }

        [MenuItem("Window/Codex MCP Bridge/Start")]
        public static void StartServer()
        {
            if (_running)
            {
                return;
            }

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://{PrefixHost}:{_port}/");
                _listener.Start();
                _running = true;

                _listenerThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "Codex MCP Bridge HTTP"
                };
                _listenerThread.Start();

                Debug.Log($"Codex MCP Bridge listening on http://{PrefixHost}:{_port}");
            }
            catch (Exception ex)
            {
                _running = false;
                Debug.LogError($"Failed to start Codex MCP Bridge: {ex.Message}");
            }
        }

        [MenuItem("Window/Codex MCP Bridge/Stop")]
        public static void StopServer()
        {
            _running = false;

            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch
            {
                // Listener may already be closed during domain reload.
            }

            _listener = null;
            Debug.Log("Codex MCP Bridge stopped");
        }

        [MenuItem("Window/Codex MCP Bridge/Status")]
        public static void LogStatus()
        {
            Debug.Log(_running
                ? $"Codex MCP Bridge is running on http://{PrefixHost}:{_port}"
                : "Codex MCP Bridge is stopped");
        }

        [MenuItem("Window/Codex MCP Bridge/Update Package From Git")]
        public static void UpdatePackageFromGit()
        {
            if (_packageUpdateRequest != null && !_packageUpdateRequest.IsCompleted)
            {
                Debug.Log("Codex MCP Bridge package update is already running");
                return;
            }

            if (!EditorUtility.DisplayDialog(
                    "Update Codex MCP Bridge",
                    "This will ask Unity Package Manager to update com.codex.unity-mcp from the GitHub main branch.",
                    "Update",
                    "Cancel"))
            {
                return;
            }

            _packageUpdateRequest = Client.Add(GitPackageUrl);
            EditorApplication.update += WatchPackageUpdate;
            Debug.Log($"Updating Codex MCP Bridge package from {GitPackageUrl}");
        }

        [MenuItem("Tools/Codex MCP Bridge/Start")]
        private static void StartServerFromToolsMenu()
        {
            StartServer();
        }

        [MenuItem("Tools/Codex MCP Bridge/Stop")]
        private static void StopServerFromToolsMenu()
        {
            StopServer();
        }

        [MenuItem("Tools/Codex MCP Bridge/Status")]
        private static void LogStatusFromToolsMenu()
        {
            LogStatus();
        }

        [MenuItem("Tools/Codex MCP Bridge/Update Package From Git")]
        private static void UpdatePackageFromGitToolsMenu()
        {
            UpdatePackageFromGit();
        }

        private static void WatchPackageUpdate()
        {
            if (_packageUpdateRequest == null || !_packageUpdateRequest.IsCompleted)
            {
                return;
            }

            EditorApplication.update -= WatchPackageUpdate;
            if (_packageUpdateRequest.Status == StatusCode.Success)
            {
                Debug.Log($"Codex MCP Bridge package updated: {_packageUpdateRequest.Result.packageId}");
            }
            else
            {
                Debug.LogError($"Failed to update Codex MCP Bridge package: {_packageUpdateRequest.Error.message}");
            }

            _packageUpdateRequest = null;
        }

        private static void ListenLoop()
        {
            while (_running && _listener != null)
            {
                try
                {
                    var context = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleContext(context));
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Codex MCP Bridge listener error: {ex.Message}");
                }
            }
        }

        private static void HandleContext(HttpListenerContext context)
        {
            try
            {
                AddCorsHeaders(context.Response);

                if (context.Request.HttpMethod == "OPTIONS")
                {
                    WriteJson(context.Response, new Dictionary<string, object> { { "ok", true } });
                    return;
                }

                var path = context.Request.Url.AbsolutePath.TrimEnd('/');
                if (string.IsNullOrEmpty(path))
                {
                    path = "/";
                }

                if (path == "/health")
                {
                    WriteJson(context.Response, new Dictionary<string, object>
                    {
                        { "ok", true },
                        { "bridge", "codex-unity-mcp" },
                        { "port", _port },
                        { "timeUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }
                    });
                    return;
                }

                var query = ParseQuery(context.Request.Url.Query);
                var result = EnqueueAndWait(() => Dispatch(path, query));
                WriteJson(context.Response, result);
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = 500;
                WriteJson(context.Response, new Dictionary<string, object>
                {
                    { "ok", false },
                    { "error", ex.GetType().Name },
                    { "message", ex.Message }
                });
            }
        }

        private static object Dispatch(string path, Dictionary<string, string> query)
        {
            switch (path)
            {
                case "/editor/state":
                    return GetEditorState();
                case "/scene/hierarchy":
                    return GetSceneHierarchy(query);
                case "/scene/find":
                    return FindGameObjects(query);
                case "/scene/gameobject":
                    return InspectGameObject(query);
                case "/scene/set-transform":
                    return SetTransform(query);
                case "/scene/create-gameobject":
                    return CreateGameObject(query);
                case "/scene/delete-gameobject":
                    return DeleteGameObject(query);
                case "/scene/duplicate-gameobject":
                    return DuplicateGameObject(query);
                case "/console":
                    return ReadConsole(query);
                case "/screenshot":
                    return CaptureScreenshot(query);
                default:
                    throw new InvalidOperationException($"Unknown endpoint: {path}");
            }
        }

        private static object EnqueueAndWait(Func<object> action)
        {
            var request = new BridgeRequest(action);
            Requests.Enqueue(request);

            try
            {
                if (!request.Completion.Task.Wait(TimeSpan.FromSeconds(30)))
                {
                    throw new TimeoutException("Unity main thread did not respond within 30 seconds");
                }
            }
            catch (AggregateException)
            {
                // Preserve the original exception below through GetAwaiter().GetResult().
            }

            return request.Completion.Task.GetAwaiter().GetResult();
        }

        private static void ProcessRequests()
        {
            var processed = 0;
            while (processed < 32 && Requests.TryDequeue(out var request))
            {
                processed++;
                try
                {
                    request.Completion.TrySetResult(request.Action());
                }
                catch (Exception ex)
                {
                    request.Completion.TrySetException(ex);
                }
            }
        }

        private static Dictionary<string, object> GetEditorState()
        {
            var scene = SceneManager.GetActiveScene();
            var selected = new List<object>();
            foreach (var obj in Selection.gameObjects)
            {
                selected.Add(GameObjectSummary(obj));
            }

            return Ok(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "isPlaying", EditorApplication.isPlaying },
                { "isPaused", EditorApplication.isPaused },
                { "isCompiling", EditorApplication.isCompiling },
                { "isUpdating", EditorApplication.isUpdating },
                { "activeScene", new Dictionary<string, object>
                    {
                        { "name", scene.name },
                        { "path", scene.path },
                        { "isDirty", scene.isDirty },
                        { "isLoaded", scene.isLoaded },
                        { "rootCount", scene.rootCount }
                    }
                },
                { "selection", selected }
            });
        }

        private static Dictionary<string, object> GetSceneHierarchy(Dictionary<string, string> query)
        {
            var includeInactive = Bool(query, "includeInactive", true);
            var maxNodes = Int(query, "maxNodes", 500);
            var scene = SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();
            var items = new List<object>();
            var count = 0;
            var truncated = false;

            foreach (var root in roots)
            {
                var item = BuildHierarchyNode(root, includeInactive, maxNodes, ref count, ref truncated);
                if (item != null)
                {
                    items.Add(item);
                }

                if (truncated)
                {
                    break;
                }
            }

            return Ok(new Dictionary<string, object>
            {
                { "scene", scene.name },
                { "path", scene.path },
                { "count", count },
                { "truncated", truncated },
                { "roots", items }
            });
        }

        private static object BuildHierarchyNode(GameObject go, bool includeInactive, int maxNodes, ref int count, ref bool truncated)
        {
            if (truncated || count >= maxNodes)
            {
                truncated = true;
                return null;
            }

            if (!includeInactive && !go.activeInHierarchy)
            {
                return null;
            }

            count++;
            var children = new List<object>();
            foreach (Transform child in go.transform)
            {
                var node = BuildHierarchyNode(child.gameObject, includeInactive, maxNodes, ref count, ref truncated);
                if (node != null)
                {
                    children.Add(node);
                }

                if (truncated)
                {
                    break;
                }
            }

            var componentTypes = new List<object>();
            foreach (var component in go.GetComponents<Component>())
            {
                componentTypes.Add(component == null ? "<missing>" : component.GetType().Name);
            }

            return new Dictionary<string, object>
            {
                { "id", go.GetInstanceID() },
                { "name", go.name },
                { "path", GetPath(go) },
                { "activeSelf", go.activeSelf },
                { "activeInHierarchy", go.activeInHierarchy },
                { "tag", SafeTag(go) },
                { "layer", LayerMask.LayerToName(go.layer) },
                { "components", componentTypes },
                { "children", children }
            };
        }

        private static Dictionary<string, object> FindGameObjects(Dictionary<string, string> query)
        {
            var text = Get(query, "query", string.Empty);
            var mode = Get(query, "mode", "name").ToLowerInvariant();
            var includeInactive = Bool(query, "includeInactive", true);
            var limit = Int(query, "limit", 50);
            var results = new List<object>();

            foreach (var go in SceneGameObjects(includeInactive))
            {
                if (results.Count >= limit)
                {
                    break;
                }

                if (Matches(go, text, mode))
                {
                    results.Add(GameObjectSummary(go));
                }
            }

            return Ok(new Dictionary<string, object>
            {
                { "query", text },
                { "mode", mode },
                { "count", results.Count },
                { "items", results }
            });
        }

        private static Dictionary<string, object> InspectGameObject(Dictionary<string, string> query)
        {
            var go = ResolveGameObject(query);
            if (go == null)
            {
                return Fail("not_found", "GameObject not found");
            }

            var includeProperties = Bool(query, "includeProperties", true);
            return Ok(GameObjectDetails(go, includeProperties));
        }

        private static Dictionary<string, object> SetTransform(Dictionary<string, string> query)
        {
            var go = ResolveGameObject(query);
            if (go == null)
            {
                return Fail("not_found", "GameObject not found");
            }

            Undo.RecordObject(go.transform, "Codex MCP Set Transform");

            if (TryVector3(query, "position", out var position))
            {
                go.transform.position = position;
            }

            if (TryVector3(query, "rotation", out var rotation))
            {
                go.transform.eulerAngles = rotation;
            }

            if (TryVector3(query, "scale", out var scale))
            {
                go.transform.localScale = scale;
            }

            EditorUtility.SetDirty(go.transform);
            EditorSceneManager.MarkSceneDirty(go.scene);

            return Ok(GameObjectDetails(go, false));
        }

        private static Dictionary<string, object> CreateGameObject(Dictionary<string, string> query)
        {
            var name = Get(query, "name", "GameObject");
            var primitiveType = Get(query, "primitiveType", "empty");
            var parent = ResolveParentGameObject(query);
            if (HasParentQuery(query) && parent == null)
            {
                return Fail("parent_not_found", "Parent GameObject not found");
            }

            GameObject go;
            if (string.Equals(primitiveType, "empty", StringComparison.OrdinalIgnoreCase))
            {
                go = new GameObject(name);
            }
            else if (TryPrimitiveType(primitiveType, out var primitive))
            {
                go = GameObject.CreatePrimitive(primitive);
                go.name = name;
            }
            else
            {
                return Fail("invalid_primitive_type", "primitiveType must be one of empty, cube, sphere, capsule, cylinder, plane, or quad");
            }

            Undo.RegisterCreatedObjectUndo(go, "Codex MCP Create GameObject");

            if (parent != null)
            {
                Undo.SetTransformParent(go.transform, parent.transform, "Codex MCP Set Parent");
            }

            ApplyOptionalTransform(go, query);
            Selection.activeGameObject = go;
            EditorSceneManager.MarkSceneDirty(go.scene);

            return Ok(GameObjectDetails(go, false));
        }

        private static Dictionary<string, object> DeleteGameObject(Dictionary<string, string> query)
        {
            var go = ResolveGameObject(query);
            if (go == null)
            {
                return Fail("not_found", "GameObject not found");
            }

            var scene = go.scene;
            var summary = GameObjectSummary(go);
            Undo.DestroyObjectImmediate(go);

            if (scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(scene);
            }

            return Ok(new Dictionary<string, object>
            {
                { "deleted", summary }
            });
        }

        private static Dictionary<string, object> DuplicateGameObject(Dictionary<string, string> query)
        {
            var source = ResolveGameObject(query);
            if (source == null)
            {
                return Fail("not_found", "GameObject not found");
            }

            var parent = ResolveParentGameObject(query);
            if (HasParentQuery(query) && parent == null)
            {
                return Fail("parent_not_found", "Parent GameObject not found");
            }

            var duplicate = Object.Instantiate(source);
            duplicate.name = Get(query, "newName", $"{source.name} Copy");
            Undo.RegisterCreatedObjectUndo(duplicate, "Codex MCP Duplicate GameObject");

            if (parent != null)
            {
                Undo.SetTransformParent(duplicate.transform, parent.transform, "Codex MCP Set Parent");
            }
            else if (source.transform.parent != null)
            {
                Undo.SetTransformParent(duplicate.transform, source.transform.parent, "Codex MCP Set Parent");
            }

            ApplyOptionalTransform(duplicate, query);
            Selection.activeGameObject = duplicate;
            EditorSceneManager.MarkSceneDirty(duplicate.scene);

            return Ok(GameObjectDetails(duplicate, false));
        }

        private static Dictionary<string, object> ReadConsole(Dictionary<string, string> query)
        {
            var count = Int(query, "count", 50);
            var entries = new List<object>();

            try
            {
                var logEntriesType = Type.GetType("UnityEditor.LogEntries,UnityEditor");
                var logEntryType = Type.GetType("UnityEditor.LogEntry,UnityEditor");
                if (logEntriesType == null || logEntryType == null)
                {
                    return Fail("console_unavailable", "Unity LogEntries reflection API is unavailable in this Unity version");
                }

                var getCount = logEntriesType.GetMethod("GetCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var getEntry = logEntriesType.GetMethod("GetEntryInternal", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var start = logEntriesType.GetMethod("StartGettingEntries", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var end = logEntriesType.GetMethod("EndGettingEntries", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                var total = (int)getCount.Invoke(null, null);
                var from = Math.Max(0, total - count);
                var entry = Activator.CreateInstance(logEntryType);

                start?.Invoke(null, null);
                for (var i = from; i < total; i++)
                {
                    getEntry.Invoke(null, new[] { (object)i, entry });
                    entries.Add(new Dictionary<string, object>
                    {
                        { "index", i },
                        { "condition", Field(entry, "condition") },
                        { "stackTrace", Field(entry, "stackTrace") },
                        { "file", Field(entry, "file") },
                        { "line", Field(entry, "line") },
                        { "mode", Field(entry, "mode") },
                        { "instanceId", Field(entry, "instanceID") }
                    });
                }
                end?.Invoke(null, null);
            }
            catch (Exception ex)
            {
                return Fail("console_error", ex.Message);
            }

            return Ok(new Dictionary<string, object>
            {
                { "count", entries.Count },
                { "items", entries }
            });
        }

        private static Dictionary<string, object> CaptureScreenshot(Dictionary<string, string> query)
        {
            var source = Get(query, "source", "scene_view");
            var includeImage = Bool(query, "includeImage", false);
            var maxResolution = Mathf.Clamp(Int(query, "maxResolution", 512), 64, 2048);
            var camera = ResolveCamera(source);

            if (camera == null)
            {
                return Fail("camera_not_found", $"No camera available for source '{source}'");
            }

            var width = maxResolution;
            var height = Mathf.Max(64, Mathf.RoundToInt(maxResolution * 9f / 16f));
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;

            try
            {
                camera.targetTexture = rt;
                RenderTexture.active = rt;
                camera.Render();

                var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();

                var bytes = texture.EncodeToPNG();
                Object.DestroyImmediate(texture);

                var dir = Path.Combine(Application.dataPath, "..", "Library", "CodexMcp", "Screenshots");
                Directory.CreateDirectory(dir);
                var fileName = $"screenshot_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.png";
                var filePath = Path.GetFullPath(Path.Combine(dir, fileName));
                File.WriteAllBytes(filePath, bytes);

                var payload = new Dictionary<string, object>
                {
                    { "source", source },
                    { "path", filePath },
                    { "width", width },
                    { "height", height }
                };

                if (includeImage)
                {
                    payload["imageBase64"] = Convert.ToBase64String(bytes);
                }

                return Ok(payload);
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                Object.DestroyImmediate(rt);
            }
        }

        private static Camera ResolveCamera(string source)
        {
            if (string.Equals(source, "main_camera", StringComparison.OrdinalIgnoreCase))
            {
                return Camera.main;
            }

            if (SceneView.lastActiveSceneView != null)
            {
                return SceneView.lastActiveSceneView.camera;
            }

            return Camera.main;
        }

        private static Dictionary<string, object> GameObjectDetails(GameObject go, bool includeProperties)
        {
            var components = new List<object>();
            foreach (var component in go.GetComponents<Component>())
            {
                components.Add(ComponentDetails(component, includeProperties));
            }

            return new Dictionary<string, object>
            {
                { "id", go.GetInstanceID() },
                { "name", go.name },
                { "path", GetPath(go) },
                { "activeSelf", go.activeSelf },
                { "activeInHierarchy", go.activeInHierarchy },
                { "tag", SafeTag(go) },
                { "layer", LayerMask.LayerToName(go.layer) },
                { "scene", go.scene.name },
                { "transform", TransformData(go.transform) },
                { "components", components }
            };
        }

        private static Dictionary<string, object> ComponentDetails(Component component, bool includeProperties)
        {
            if (component == null)
            {
                return new Dictionary<string, object> { { "type", "<missing>" } };
            }

            var data = new Dictionary<string, object>
            {
                { "type", component.GetType().FullName },
                { "enabled", ComponentEnabled(component) }
            };

            var mono = component as MonoBehaviour;
            if (mono != null && MonoScript.FromMonoBehaviour(mono) != null)
            {
                data["scriptPath"] = AssetDatabase.GetAssetPath(MonoScript.FromMonoBehaviour(mono));
            }

            if (includeProperties)
            {
                data["properties"] = SerializedProperties(component, 128);
            }

            return data;
        }

        private static Dictionary<string, object> SerializedProperties(Object target, int limit)
        {
            var result = new Dictionary<string, object>();
            try
            {
                var serialized = new SerializedObject(target);
                var prop = serialized.GetIterator();
                var entered = true;
                var count = 0;

                while (prop.NextVisible(entered) && count < limit)
                {
                    entered = false;
                    if (prop.propertyPath == "m_Script")
                    {
                        continue;
                    }

                    var value = SerializedValue(prop);
                    if (value != null)
                    {
                        result[prop.propertyPath] = value;
                        count++;
                    }
                }

                if (count >= limit)
                {
                    result["_truncated"] = true;
                }
            }
            catch (Exception ex)
            {
                result["_error"] = ex.Message;
            }

            return result;
        }

        private static object SerializedValue(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return prop.intValue;
                case SerializedPropertyType.Boolean:
                    return prop.boolValue;
                case SerializedPropertyType.Float:
                    return prop.floatValue;
                case SerializedPropertyType.String:
                    return prop.stringValue;
                case SerializedPropertyType.Color:
                    return Vec4(prop.colorValue);
                case SerializedPropertyType.ObjectReference:
                    return prop.objectReferenceValue == null
                        ? null
                        : new Dictionary<string, object>
                        {
                            { "name", prop.objectReferenceValue.name },
                            { "type", prop.objectReferenceValue.GetType().Name },
                            { "assetPath", AssetDatabase.GetAssetPath(prop.objectReferenceValue) },
                            { "instanceId", prop.objectReferenceInstanceIDValue }
                        };
                case SerializedPropertyType.LayerMask:
                    return prop.intValue;
                case SerializedPropertyType.Enum:
                    return prop.enumDisplayNames != null && prop.enumValueIndex >= 0 && prop.enumValueIndex < prop.enumDisplayNames.Length
                        ? prop.enumDisplayNames[prop.enumValueIndex]
                        : prop.enumValueIndex.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Vector2:
                    return Vec2(prop.vector2Value);
                case SerializedPropertyType.Vector3:
                    return Vec3(prop.vector3Value);
                case SerializedPropertyType.Vector4:
                    return Vec4(prop.vector4Value);
                case SerializedPropertyType.Rect:
                    return new Dictionary<string, object>
                    {
                        { "x", prop.rectValue.x },
                        { "y", prop.rectValue.y },
                        { "width", prop.rectValue.width },
                        { "height", prop.rectValue.height }
                    };
                case SerializedPropertyType.Bounds:
                    return new Dictionary<string, object>
                    {
                        { "center", Vec3(prop.boundsValue.center) },
                        { "size", Vec3(prop.boundsValue.size) }
                    };
                case SerializedPropertyType.Quaternion:
                    return QuaternionValue(prop.quaternionValue);
                default:
                    return null;
            }
        }

        private static List<GameObject> SceneGameObjects(bool includeInactive)
        {
            var list = new List<GameObject>();
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (!go.scene.IsValid() || !go.scene.isLoaded)
                {
                    continue;
                }

                if (!includeInactive && !go.activeInHierarchy)
                {
                    continue;
                }

                list.Add(go);
            }

            list.Sort((a, b) => string.Compare(GetPath(a), GetPath(b), StringComparison.OrdinalIgnoreCase));
            return list;
        }

        private static bool Matches(GameObject go, string text, string mode)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            if (mode == "tag")
            {
                return string.Equals(SafeTag(go), text, StringComparison.OrdinalIgnoreCase);
            }

            if (mode == "component")
            {
                foreach (var component in go.GetComponents<Component>())
                {
                    if (component != null && component.GetType().Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
                return false;
            }

            if (mode == "path")
            {
                return GetPath(go).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return go.name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static GameObject ResolveGameObject(Dictionary<string, string> query)
        {
            if (query.TryGetValue("id", out var rawId) && int.TryParse(rawId, out var id))
            {
                var obj = EditorUtility.InstanceIDToObject(id) as GameObject;
                if (obj != null)
                {
                    return obj;
                }
            }

            if (query.TryGetValue("path", out var path))
            {
                foreach (var go in SceneGameObjects(true))
                {
                    if (string.Equals(GetPath(go), path, StringComparison.OrdinalIgnoreCase))
                    {
                        return go;
                    }
                }
            }

            if (query.TryGetValue("name", out var name))
            {
                foreach (var go in SceneGameObjects(true))
                {
                    if (string.Equals(go.name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return go;
                    }
                }
            }

            return null;
        }

        private static GameObject ResolveParentGameObject(Dictionary<string, string> query)
        {
            var parentQuery = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (query.TryGetValue("parentId", out var parentId))
            {
                parentQuery["id"] = parentId;
            }

            if (query.TryGetValue("parentPath", out var parentPath))
            {
                parentQuery["path"] = parentPath;
            }

            if (query.TryGetValue("parentName", out var parentName))
            {
                parentQuery["name"] = parentName;
            }

            return parentQuery.Count == 0 ? null : ResolveGameObject(parentQuery);
        }

        private static bool HasParentQuery(Dictionary<string, string> query)
        {
            return query.ContainsKey("parentId") || query.ContainsKey("parentPath") || query.ContainsKey("parentName");
        }

        private static void ApplyOptionalTransform(GameObject go, Dictionary<string, string> query)
        {
            if (TryVector3(query, "position", out var position))
            {
                go.transform.position = position;
            }

            if (TryVector3(query, "rotation", out var rotation))
            {
                go.transform.eulerAngles = rotation;
            }

            if (TryVector3(query, "scale", out var scale))
            {
                go.transform.localScale = scale;
            }

            EditorUtility.SetDirty(go.transform);
        }

        private static bool TryPrimitiveType(string value, out PrimitiveType primitive)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "cube":
                    primitive = PrimitiveType.Cube;
                    return true;
                case "sphere":
                    primitive = PrimitiveType.Sphere;
                    return true;
                case "capsule":
                    primitive = PrimitiveType.Capsule;
                    return true;
                case "cylinder":
                    primitive = PrimitiveType.Cylinder;
                    return true;
                case "plane":
                    primitive = PrimitiveType.Plane;
                    return true;
                case "quad":
                    primitive = PrimitiveType.Quad;
                    return true;
                default:
                    primitive = default;
                    return false;
            }
        }

        private static Dictionary<string, object> GameObjectSummary(GameObject go)
        {
            return new Dictionary<string, object>
            {
                { "id", go.GetInstanceID() },
                { "name", go.name },
                { "path", GetPath(go) },
                { "activeSelf", go.activeSelf },
                { "activeInHierarchy", go.activeInHierarchy }
            };
        }

        private static Dictionary<string, object> TransformData(Transform transform)
        {
            return new Dictionary<string, object>
            {
                { "position", Vec3(transform.position) },
                { "localPosition", Vec3(transform.localPosition) },
                { "rotation", Vec3(transform.eulerAngles) },
                { "localRotation", Vec3(transform.localEulerAngles) },
                { "scale", Vec3(transform.localScale) }
            };
        }

        private static string GetPath(GameObject go)
        {
            var names = new Stack<string>();
            var current = go.transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }
            return string.Join("/", names.ToArray());
        }

        private static object ComponentEnabled(Component component)
        {
            if (component is Behaviour behaviour)
            {
                return behaviour.enabled;
            }

            if (component is Renderer renderer)
            {
                return renderer.enabled;
            }

            if (component is Collider collider)
            {
                return collider.enabled;
            }

            return null;
        }

        private static object Field(object instance, string name)
        {
            var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(instance);
        }

        private static string SafeTag(GameObject go)
        {
            try
            {
                return go.tag;
            }
            catch
            {
                return "Untagged";
            }
        }

        private static Dictionary<string, object> Ok(object data)
        {
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "data", data }
            };
        }

        private static Dictionary<string, object> Fail(string code, string message)
        {
            return new Dictionary<string, object>
            {
                { "ok", false },
                { "error", code },
                { "message", message }
            };
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query))
            {
                return result;
            }

            foreach (var part in query.TrimStart('?').Split('&'))
            {
                if (string.IsNullOrEmpty(part))
                {
                    continue;
                }

                var pieces = part.Split(new[] { '=' }, 2);
                var key = WebUtility.UrlDecode(pieces[0]);
                var value = pieces.Length > 1 ? WebUtility.UrlDecode(pieces[1]) : string.Empty;
                result[key] = value;
            }

            return result;
        }

        private static string Get(Dictionary<string, string> query, string key, string fallback)
        {
            return query.TryGetValue(key, out var value) ? value : fallback;
        }

        private static int Int(Dictionary<string, string> query, string key, int fallback)
        {
            return query.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
        }

        private static bool Bool(Dictionary<string, string> query, string key, bool fallback)
        {
            if (!query.TryGetValue(key, out var value))
            {
                return fallback;
            }

            return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryVector3(Dictionary<string, string> query, string key, out Vector3 value)
        {
            value = default;
            if (!query.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var parts = raw.Split(',');
            if (parts.Length != 3)
            {
                return false;
            }

            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            {
                return false;
            }

            value = new Vector3(x, y, z);
            return true;
        }

        private static List<object> Vec2(Vector2 v)
        {
            return new List<object> { v.x, v.y };
        }

        private static List<object> Vec3(Vector3 v)
        {
            return new List<object> { v.x, v.y, v.z };
        }

        private static List<object> Vec4(Vector4 v)
        {
            return new List<object> { v.x, v.y, v.z, v.w };
        }

        private static List<object> QuaternionValue(Quaternion q)
        {
            return new List<object> { q.x, q.y, q.z, q.w };
        }

        private static void AddCorsHeaders(HttpListenerResponse response)
        {
            response.Headers["Access-Control-Allow-Origin"] = "http://127.0.0.1";
            response.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
            response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        }

        private static void WriteJson(HttpListenerResponse response, object data)
        {
            var json = MiniJson.Serialize(data);
            var bytes = Encoding.UTF8.GetBytes(json);
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.OutputStream.Close();
        }

        private sealed class BridgeRequest
        {
            public BridgeRequest(Func<object> action)
            {
                Action = action;
            }

            public Func<object> Action { get; }
            public TaskCompletionSource<object> Completion { get; } = new TaskCompletionSource<object>();
        }
    }

    internal static class MiniJson
    {
        public static string Serialize(object value)
        {
            var builder = new StringBuilder(4096);
            WriteValue(builder, value);
            return builder.ToString();
        }

        private static void WriteValue(StringBuilder builder, object value)
        {
            if (value == null)
            {
                builder.Append("null");
                return;
            }

            if (value is string str)
            {
                WriteString(builder, str);
                return;
            }

            if (value is bool boolean)
            {
                builder.Append(boolean ? "true" : "false");
                return;
            }

            if (value is IDictionary dictionary)
            {
                WriteObject(builder, dictionary);
                return;
            }

            if (value is IEnumerable enumerable && !(value is string))
            {
                WriteArray(builder, enumerable);
                return;
            }

            if (value is float || value is double || value is decimal)
            {
                builder.Append(Convert.ToDouble(value).ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            if (value is int || value is long || value is short || value is byte)
            {
                builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }

            WriteString(builder, value.ToString());
        }

        private static void WriteObject(StringBuilder builder, IDictionary dictionary)
        {
            builder.Append('{');
            var first = true;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (!first)
                {
                    builder.Append(',');
                }
                first = false;
                WriteString(builder, Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                builder.Append(':');
                WriteValue(builder, entry.Value);
            }
            builder.Append('}');
        }

        private static void WriteArray(StringBuilder builder, IEnumerable enumerable)
        {
            builder.Append('[');
            var first = true;
            foreach (var item in enumerable)
            {
                if (!first)
                {
                    builder.Append(',');
                }
                first = false;
                WriteValue(builder, item);
            }
            builder.Append(']');
        }

        private static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (c < 32)
                        {
                            builder.Append("\\u");
                            builder.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }
                        break;
                }
            }
            builder.Append('"');
        }
    }
}
