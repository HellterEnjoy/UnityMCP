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
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;
#if ENABLE_INPUT_SYSTEM
using InputKey = UnityEngine.InputSystem.Key;
using InputKeyboard = UnityEngine.InputSystem.Keyboard;
using InputMouse = UnityEngine.InputSystem.Mouse;
using InputSystemApi = UnityEngine.InputSystem.InputSystem;
using InputKeyboardState = UnityEngine.InputSystem.LowLevel.KeyboardState;
using InputMouseButton = UnityEngine.InputSystem.LowLevel.MouseButton;
using InputMouseState = UnityEngine.InputSystem.LowLevel.MouseState;
#endif
using Object = UnityEngine.Object;

#pragma warning disable 0618

namespace CodexUnityMcp
{
    [InitializeOnLoad]
    public static class CodexMcpBridge
    {
        private const int DefaultPort = 8765;
        private const string PrefixHost = "127.0.0.1";
        private const string ProductName = "Unity MCP";
        private const string PackageName = "com.codex.unity-mcp";
        private const string GitPackageUrl = "https://github.com/HellterEnjoy/UnityMCP.git?path=/unity-package/Packages/com.codex.unity-mcp#main";
        private const string RepositoryUrl = "https://github.com/HellterEnjoy/UnityMCP";
        private const string WindowMenuRoot = "Window/Unity MCP";
        private const string ToolsMenuRoot = "Tools/Unity MCP";
        private const int MaxRecentLogs = 256;
        private static readonly ConcurrentQueue<BridgeRequest> Requests = new ConcurrentQueue<BridgeRequest>();
        private static readonly Dictionary<string, SceneSnapshot> Snapshots = new Dictionary<string, SceneSnapshot>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ConsoleCheckpoint> ConsoleCheckpoints = new Dictionary<string, ConsoleCheckpoint>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, EditorSessionState> EditorSessions = new Dictionary<string, EditorSessionState>(StringComparer.OrdinalIgnoreCase);
        private static readonly object RecentLogsLock = new object();
        private static readonly List<RuntimeLogEntry> RecentLogs = new List<RuntimeLogEntry>();
        private static readonly object TestRunLock = new object();
#if ENABLE_INPUT_SYSTEM
        private static readonly HashSet<InputKey> PressedInputKeys = new HashSet<InputKey>();
        private static readonly HashSet<int> PressedMouseButtons = new HashSet<int>();
        private static Vector2 _lastInjectedMousePosition;
#endif

        private static HttpListener _listener;
        private static Thread _listenerThread;
        private static volatile bool _running;
        private static int _port;
        private static AddRequest _packageUpdateRequest;
        private static TestRunState _currentTestRun;
        private static double _nextAutoStartTime;
        private static double _lastPortBusyWarningTime;
        private static bool? _pendingPlayModeRequest;

        static CodexMcpBridge()
        {
            _port = EditorPrefs.GetInt("CodexMcpBridge.Port", DefaultPort);
            AssemblyReloadEvents.beforeAssemblyReload += StopServer;
            EditorApplication.quitting += StopServer;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            Application.logMessageReceivedThreaded += CaptureRuntimeLog;
            EditorApplication.update += ProcessRequests;
            EditorApplication.update += AutoStartServerIfNeeded;
            _nextAutoStartTime = EditorApplication.timeSinceStartup;
            EditorApplication.delayCall += StartServer;
        }

        [MenuItem(WindowMenuRoot + "/Bridge/Start", false, 2100)]
        public static void StartServer()
        {
            StartServerInternal(true);
        }

        private static void StartServerInternal(bool logFailures)
        {
            if (_running)
            {
                return;
            }

            if (_listener != null)
            {
                StopServerInternal(false);
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
                    Name = ProductName + " HTTP"
                };
                _listenerThread.Start();

                Debug.Log($"{ProductName} listening on http://{PrefixHost}:{_port}");
            }
            catch (HttpListenerException ex) when (IsAddressAlreadyInUse(ex))
            {
                CleanupListenerState();
                var existingBridgeStatus = DetectExistingBridgeStatus();
                if (existingBridgeStatus == ExistingBridgeStatus.Reachable)
                {
                    _nextAutoStartTime = EditorApplication.timeSinceStartup + 10.0d;
                    return;
                }

                _nextAutoStartTime = EditorApplication.timeSinceStartup + 5.0d;
                if (logFailures)
                {
                    var detail = existingBridgeStatus == ExistingBridgeStatus.PortBusy
                        ? $"{ex.Message} The port is already occupied by another listener."
                        : ex.Message;
                    Debug.LogError($"Failed to start {ProductName}: {detail}");
                }
                else
                {
                    MaybeLogAutoStartBusyWarning(existingBridgeStatus);
                }
            }
            catch (Exception ex)
            {
                CleanupListenerState();
                _nextAutoStartTime = EditorApplication.timeSinceStartup + 2.0d;
                if (logFailures)
                {
                    Debug.LogError($"Failed to start {ProductName}: {ex.Message}");
                }
                else
                {
                    Debug.LogWarning($"{ProductName} auto-start failed: {ex.Message}");
                }
            }
        }

        [MenuItem(WindowMenuRoot + "/Bridge/Stop", false, 2101)]
        public static void StopServer()
        {
            StopServerInternal(true);
        }

        private static void StopServerInternal(bool logStop)
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

            if (_listenerThread != null && _listenerThread.IsAlive)
            {
                try
                {
                    _listenerThread.Join(250);
                }
                catch
                {
                    // Best effort only.
                }
            }

            CleanupListenerState();
            _nextAutoStartTime = EditorApplication.timeSinceStartup + 1.0d;
            if (logStop)
            {
                Debug.Log($"{ProductName} stopped");
            }
        }

        [MenuItem(WindowMenuRoot + "/Bridge/Status", false, 2102)]
        public static void LogStatus()
        {
            Debug.Log(_running
                ? $"{ProductName} is running on http://{PrefixHost}:{_port}"
                : $"{ProductName} is stopped");
        }

        [MenuItem(WindowMenuRoot + "/Package/Update From Git", false, 2110)]
        public static void UpdatePackageFromGit()
        {
            if (_packageUpdateRequest != null && !_packageUpdateRequest.IsCompleted)
            {
                Debug.Log($"{ProductName} package update is already running");
                return;
            }

            if (!EditorUtility.DisplayDialog(
                    $"Update {ProductName}",
                    $"This will ask Unity Package Manager to update {PackageName} from the GitHub main branch.",
                    "Update",
                    "Cancel"))
            {
                return;
            }

            _packageUpdateRequest = Client.Add(GitPackageUrl);
            EditorApplication.update += WatchPackageUpdate;
            Debug.Log($"Updating {ProductName} package from {GitPackageUrl}");
        }

        [MenuItem(ToolsMenuRoot + "/Bridge/Start", false, 1300)]
        private static void StartServerFromToolsMenu()
        {
            StartServer();
        }

        [MenuItem(ToolsMenuRoot + "/Bridge/Stop", false, 1301)]
        private static void StopServerFromToolsMenu()
        {
            StopServer();
        }

        [MenuItem(ToolsMenuRoot + "/Bridge/Status", false, 1302)]
        private static void LogStatusFromToolsMenu()
        {
            LogStatus();
        }

        [MenuItem(ToolsMenuRoot + "/Package/Update From Git", false, 1310)]
        private static void UpdatePackageFromGitToolsMenu()
        {
            UpdatePackageFromGit();
        }

        [MenuItem(ToolsMenuRoot + "/Package/Open GitHub Repository", false, 1311)]
        private static void OpenRepositoryFromToolsMenu()
        {
            Application.OpenURL(RepositoryUrl);
        }

        [MenuItem(ToolsMenuRoot + "/Open Screenshots Folder", false, 1320)]
        private static void OpenScreenshotsFolderFromToolsMenu()
        {
            Directory.CreateDirectory(GetScreenshotsDirectory());
            EditorUtility.RevealInFinder(GetScreenshotsDirectory());
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
                Debug.Log($"{ProductName} package updated: {_packageUpdateRequest.Result.packageId}");
            }
            else
            {
                Debug.LogError($"Failed to update {ProductName} package: {_packageUpdateRequest.Error.message}");
            }

            _packageUpdateRequest = null;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode)
            {
                StopServerInternal(false);
                _nextAutoStartTime = EditorApplication.timeSinceStartup + 1.5d;
                return;
            }

            if (state == PlayModeStateChange.EnteredEditMode || state == PlayModeStateChange.EnteredPlayMode)
            {
                _pendingPlayModeRequest = null;
                _nextAutoStartTime = EditorApplication.timeSinceStartup + 1.0d;
            }
        }

        private static void QueuePlayModeRequest(bool enterPlayMode)
        {
            _pendingPlayModeRequest = enterPlayMode;
            EditorApplication.delayCall -= ApplyQueuedPlayModeRequest;
            EditorApplication.delayCall += ApplyQueuedPlayModeRequest;
        }

        private static void ApplyQueuedPlayModeRequest()
        {
            if (!_pendingPlayModeRequest.HasValue)
            {
                return;
            }

            var target = _pendingPlayModeRequest.Value;
            try
            {
                if (target)
                {
                    if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
                    {
                        EditorApplication.isPlaying = true;
                    }
                }
                else if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    EditorApplication.isPlaying = false;
                }
            }
            finally
            {
                if ((target && !EditorApplication.isPlayingOrWillChangePlaymode) ||
                    (!target && !EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode))
                {
                    _pendingPlayModeRequest = null;
                }
            }
        }

        private static void AutoStartServerIfNeeded()
        {
            if (_running || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return;
            }

            if (EditorApplication.timeSinceStartup < _nextAutoStartTime)
            {
                return;
            }

            _nextAutoStartTime = EditorApplication.timeSinceStartup + 2.0d;
            StartServerInternal(false);
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
                    Debug.LogWarning($"{ProductName} listener error: {ex.Message}");
                }
            }
        }

        private static void CleanupListenerState()
        {
            _running = false;
            _listener = null;
            _listenerThread = null;
        }

        private static bool IsAddressAlreadyInUse(HttpListenerException ex)
        {
            const int Wsaeaddrinuse = 10048;
            const int Win32AlreadyExists = 183;
            return ex != null && (ex.ErrorCode == Wsaeaddrinuse || ex.ErrorCode == Win32AlreadyExists);
        }

        private static ExistingBridgeStatus DetectExistingBridgeStatus()
        {
            try
            {
                var request = WebRequest.CreateHttp($"http://{PrefixHost}:{_port}/health");
                request.Method = "GET";
                request.Timeout = 500;
                request.ReadWriteTimeout = 500;

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = stream == null ? null : new StreamReader(stream))
                {
                    var body = reader == null ? string.Empty : reader.ReadToEnd();
                    return body.IndexOf("\"bridge\":\"unity-mcp\"", StringComparison.OrdinalIgnoreCase) >= 0
                        ? ExistingBridgeStatus.Reachable
                        : ExistingBridgeStatus.PortBusy;
                }
            }
            catch
            {
                return ExistingBridgeStatus.PortBusy;
            }
        }

        private static void MaybeLogAutoStartBusyWarning(ExistingBridgeStatus status)
        {
            var now = EditorApplication.timeSinceStartup;
            if (now - _lastPortBusyWarningTime < 30.0d)
            {
                return;
            }

            _lastPortBusyWarningTime = now;
            if (status == ExistingBridgeStatus.PortBusy)
            {
                Debug.LogWarning($"{ProductName} auto-start skipped because port {_port} is busy. Will retry automatically.");
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
                        { "bridge", "unity-mcp" },
                        { "port", _port },
                        { "timeUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }
                    });
                    return;
                }

                var query = ParseQuery(context.Request.Url.Query);
                var result = IsWaitPath(path)
                    ? HandleWaitRequest(path, query)
                    : EnqueueAndWait(() => Dispatch(path, query));
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
                case "/scene/add-component":
                    return AddComponentToGameObject(query);
                case "/scene/remove-component":
                    return RemoveComponentFromGameObject(query);
                case "/safe/snapshot":
                    return CreateSceneSnapshot(query);
                case "/safe/diff":
                    return DiffSceneSnapshot(query);
                case "/safe/batch":
                    return ExecuteSafeBatch(query);
                case "/play/enter":
                    return EnterPlayMode(query);
                case "/play/exit":
                    return ExitPlayMode(query);
                case "/play/state":
                    return GetPlayState();
                case "/menu/invoke":
                    return InvokeMenuItem(query);
                case "/tests/run":
                    return RunUnityTests(query);
                case "/tests/status":
                    return GetUnityTestStatus(query);
                case "/runtime/component-field":
                    return GetComponentField(query);
                case "/runtime/set-component-field":
                    return SetComponentField(query);
                case "/runtime/live-component-field":
                    return GetLiveComponentField(query);
                case "/input/keyboard":
                    return SendKeyboardInput(query);
                case "/input/mouse":
                    return SendMouseInput(query);
                case "/input/click-ui":
                    return ClickUiElement(query);
                case "/editor/screenshot":
                    return CaptureEditorScreenshot(query);
                case "/editor/full-screenshot":
                    return CaptureFullEditorScreenshot(query);
                case "/editor/focus-window":
                    return FocusEditorWindow(query);
                case "/editor/select-object":
                    return SelectSceneObject(query);
                case "/editor/select-asset":
                    return SelectAsset(query);
                case "/editor/open-asset":
                    return OpenAsset(query);
                case "/editor/reveal-asset":
                    return RevealAssetInProject(query);
                case "/editor/search-assets":
                    return SearchAssets(query);
                case "/asset/create-scriptable-object":
                    return CreateScriptableObjectAsset(query);
                case "/asset/inspect-scriptable-object":
                    return InspectScriptableObjectAsset(query);
                case "/asset/set-scriptable-object-field":
                    return SetScriptableObjectField(query);
                case "/editor/save-session":
                    return SaveEditorSession(query);
                case "/editor/restore-session":
                    return RestoreEditorSession(query);
                case "/console/checkpoint":
                    return CreateConsoleCheckpoint(query);
                case "/console/since":
                    return ReadConsoleSinceCheckpoint(query);
                case "/console/clear":
                    return ClearConsole();
                case "/console":
                    return ReadConsole(query);
                case "/screenshot":
                    return CaptureScreenshot(query);
                default:
                    throw new InvalidOperationException($"Unknown endpoint: {path}");
            }
        }

        private static bool IsWaitPath(string path)
        {
            switch (path)
            {
                case "/wait/object":
                case "/wait/log":
                case "/wait/scene":
                case "/wait/component-field":
                case "/wait/live-component-field":
                case "/wait/play-mode":
                case "/wait/tests":
                    return true;
                default:
                    return false;
            }
        }

        private static Dictionary<string, object> HandleWaitRequest(string path, Dictionary<string, string> query)
        {
            var timeoutMs = Int(query, "timeoutMs", 5000);
            var pollMs = Mathf.Clamp(Int(query, "pollMs", 100), 10, 1000);
            var startUtc = DateTime.UtcNow;
            var deadlineUtc = startUtc.AddMilliseconds(Math.Max(1, timeoutMs));
            Dictionary<string, object> last = null;

            while (DateTime.UtcNow <= deadlineUtc)
            {
                last = EnqueueAndWait(() => CheckWaitCondition(path, query)) as Dictionary<string, object>;
                if (last != null && IsOk(last))
                {
                    var data = ObjectDict(last, "data") ?? new Dictionary<string, object>();
                    data["elapsedMs"] = (int)(DateTime.UtcNow - startUtc).TotalMilliseconds;
                    return Ok(data);
                }

                Thread.Sleep(pollMs);
            }

            return last ?? Fail("wait_timeout", $"Timed out waiting for {path}");
        }

        private static Dictionary<string, object> CheckWaitCondition(string path, Dictionary<string, string> query)
        {
            switch (path)
            {
                case "/wait/object":
                    return WaitForObjectCondition(query);
                case "/wait/log":
                    return WaitForLogCondition(query);
                case "/wait/scene":
                    return WaitForSceneCondition(query);
                case "/wait/component-field":
                    return WaitForComponentFieldCondition(query);
                case "/wait/live-component-field":
                    return WaitForLiveComponentFieldCondition(query);
                case "/wait/play-mode":
                    return WaitForPlayModeCondition(query);
                case "/wait/tests":
                    return WaitForTestsCondition(query);
                default:
                    return Fail("unknown_wait_path", $"Unknown wait endpoint: {path}");
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

            Undo.RecordObject(go.transform, "Unity MCP Set Transform");

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

            Undo.RegisterCreatedObjectUndo(go, "Unity MCP Create GameObject");

            if (parent != null)
            {
                Undo.SetTransformParent(go.transform, parent.transform, "Unity MCP Set Parent");
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
            Undo.RegisterCreatedObjectUndo(duplicate, "Unity MCP Duplicate GameObject");

            if (parent != null)
            {
                Undo.SetTransformParent(duplicate.transform, parent.transform, "Unity MCP Set Parent");
            }
            else if (source.transform.parent != null)
            {
                Undo.SetTransformParent(duplicate.transform, source.transform.parent, "Unity MCP Set Parent");
            }

            ApplyOptionalTransform(duplicate, query);
            Selection.activeGameObject = duplicate;
            EditorSceneManager.MarkSceneDirty(duplicate.scene);

            return Ok(GameObjectDetails(duplicate, false));
        }

        private static Dictionary<string, object> AddComponentToGameObject(Dictionary<string, string> query)
        {
            var go = ResolveGameObject(query);
            if (go == null)
            {
                return Fail("not_found", "GameObject not found");
            }

            var componentTypeName = Get(query, "componentType", string.Empty);
            if (!TryResolveComponentType(componentTypeName, out var componentType, out var error))
            {
                return Fail("component_type_error", error);
            }

            if (typeof(Transform).IsAssignableFrom(componentType))
            {
                return Fail("cannot_add_transform", "Transform is created with every GameObject and cannot be added manually");
            }

            if (go.GetComponent(componentType) != null && !Bool(query, "allowMultiple", false))
            {
                return Fail("component_exists", $"GameObject already has component {componentType.FullName}. Pass allowMultiple=true to add another instance.");
            }

            Component component;
            try
            {
                component = Undo.AddComponent(go, componentType);
            }
            catch (Exception ex)
            {
                return Fail("add_component_failed", ex.Message);
            }

            EditorUtility.SetDirty(go);
            EditorSceneManager.MarkSceneDirty(go.scene);

            return Ok(new Dictionary<string, object>
            {
                { "gameObject", GameObjectDetails(go, false) },
                { "addedComponent", ComponentDetails(component, false) }
            });
        }

        private static Dictionary<string, object> RemoveComponentFromGameObject(Dictionary<string, string> query)
        {
            var go = ResolveGameObject(query);
            if (go == null)
            {
                return Fail("not_found", "GameObject not found");
            }

            var componentTypeName = Get(query, "componentType", string.Empty);
            if (!TryResolveComponentType(componentTypeName, out var componentType, out var error))
            {
                return Fail("component_type_error", error);
            }

            if (typeof(Transform).IsAssignableFrom(componentType))
            {
                return Fail("cannot_remove_transform", "Transform cannot be removed from a GameObject");
            }

            var removeAll = Bool(query, "removeAll", false);
            var removed = new List<object>();
            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null || !componentType.IsAssignableFrom(component.GetType()))
                {
                    continue;
                }

                removed.Add(ComponentDetails(component, false));
                Undo.DestroyObjectImmediate(component);

                if (!removeAll)
                {
                    break;
                }
            }

            if (removed.Count == 0)
            {
                return Fail("component_not_found", $"GameObject does not have component {componentType.FullName}");
            }

            EditorUtility.SetDirty(go);
            EditorSceneManager.MarkSceneDirty(go.scene);

            return Ok(new Dictionary<string, object>
            {
                { "gameObject", GameObjectDetails(go, false) },
                { "removedComponents", removed }
            });
        }

        private static Dictionary<string, object> CreateSceneSnapshot(Dictionary<string, string> query)
        {
            var id = Get(query, "id", string.Empty);
            if (string.IsNullOrWhiteSpace(id))
            {
                id = $"snapshot-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            }

            var snapshot = CaptureSceneSnapshot(id);
            Snapshots[id] = snapshot;
            return Ok(SnapshotSummary(snapshot));
        }

        private static Dictionary<string, object> DiffSceneSnapshot(Dictionary<string, string> query)
        {
            var beforeId = Get(query, "before", string.Empty);
            if (string.IsNullOrWhiteSpace(beforeId))
            {
                return Fail("snapshot_required", "before snapshot id is required");
            }

            if (!Snapshots.TryGetValue(beforeId, out var before))
            {
                return Fail("snapshot_not_found", $"Snapshot '{beforeId}' was not found");
            }

            SceneSnapshot after;
            var afterId = Get(query, "after", string.Empty);
            if (!string.IsNullOrWhiteSpace(afterId))
            {
                if (!Snapshots.TryGetValue(afterId, out after))
                {
                    return Fail("snapshot_not_found", $"Snapshot '{afterId}' was not found");
                }
            }
            else
            {
                after = CaptureSceneSnapshot("current");
            }

            return Ok(BuildSnapshotDiff(before, after));
        }

        private static Dictionary<string, object> ExecuteSafeBatch(Dictionary<string, string> query)
        {
            if (!query.TryGetValue("commands", out var rawCommands) || string.IsNullOrWhiteSpace(rawCommands))
            {
                return Fail("commands_required", "commands JSON array is required");
            }

            List<object> parsed;
            try
            {
                parsed = MiniJson.Parse(rawCommands) as List<object>;
            }
            catch (Exception ex)
            {
                return Fail("invalid_commands_json", ex.Message);
            }

            if (parsed == null)
            {
                return Fail("invalid_commands_json", "commands must be a JSON array");
            }

            var rollbackOnError = Bool(query, "rollbackOnError", true);
            var label = Get(query, "label", "Unity MCP Safe Batch");
            var before = CaptureSceneSnapshot("before");
            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(label);

            var results = new List<object>();
            var success = true;
            var failedIndex = -1;

            for (var i = 0; i < parsed.Count; i++)
            {
                var command = parsed[i] as Dictionary<string, object>;
                if (command == null)
                {
                    results.Add(new Dictionary<string, object>
                    {
                        { "ok", false },
                        { "error", "invalid_command" },
                        { "message", "Command must be an object" }
                    });
                    success = false;
                    failedIndex = i;
                    break;
                }

                var commandResult = ExecuteSafeBatchCommand(command);
                results.Add(commandResult);
                if (!IsOk(commandResult))
                {
                    success = false;
                    failedIndex = i;
                    break;
                }
            }

            Undo.CollapseUndoOperations(undoGroup);

            var rolledBack = false;
            if (!success && rollbackOnError)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                rolledBack = true;
            }

            var after = CaptureSceneSnapshot("after");
            return Ok(new Dictionary<string, object>
            {
                { "success", success },
                { "rolledBack", rolledBack },
                { "failedIndex", failedIndex },
                { "results", results },
                { "diff", BuildSnapshotDiff(before, after) }
            });
        }

        private static Dictionary<string, object> EnterPlayMode(Dictionary<string, string> query)
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode || _pendingPlayModeRequest == true)
            {
                return Ok(PlayStatePayload(false));
            }

            QueuePlayModeRequest(true);
            return Ok(PlayStatePayload(true));
        }

        private static Dictionary<string, object> ExitPlayMode(Dictionary<string, string> query)
        {
            if ((!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode) || _pendingPlayModeRequest == false)
            {
                return Ok(PlayStatePayload(false));
            }

            QueuePlayModeRequest(false);
            return Ok(PlayStatePayload(true));
        }

        private static Dictionary<string, object> GetPlayState()
        {
            return Ok(PlayStatePayload(false));
        }

        private static Dictionary<string, object> PlayStatePayload(bool requestedChange)
        {
            return new Dictionary<string, object>
            {
                { "requestedChange", requestedChange },
                { "isPlaying", EditorApplication.isPlaying },
                { "isPaused", EditorApplication.isPaused },
                { "isPlayingOrWillChangePlaymode", EditorApplication.isPlayingOrWillChangePlaymode },
                { "pendingRequest", _pendingPlayModeRequest.HasValue ? (_pendingPlayModeRequest.Value ? "enter" : "exit") : string.Empty }
            };
        }

        private static Dictionary<string, object> InvokeMenuItem(Dictionary<string, string> query)
        {
            var menuPath = Get(query, "menuPath", string.Empty);
            if (string.IsNullOrWhiteSpace(menuPath))
            {
                return Fail("menu_path_required", "menuPath is required");
            }

            var invoked = EditorApplication.ExecuteMenuItem(menuPath);
            if (!invoked)
            {
                return Fail("menu_not_found", $"Unity menu item '{menuPath}' was not found or could not be invoked");
            }

            return Ok(new Dictionary<string, object>
            {
                { "menuPath", menuPath },
                { "invoked", true }
            });
        }

        private static Dictionary<string, object> RunUnityTests(Dictionary<string, string> query)
        {
            lock (TestRunLock)
            {
                if (_currentTestRun != null && !_currentTestRun.IsComplete)
                {
                    return Fail("tests_already_running", $"Test run '{_currentTestRun.Id}' is still running");
                }
            }

            var mode = Get(query, "mode", "editmode").Trim().ToLowerInvariant();
            var filter = BuildTestFilter(query, mode);
            var settings = new ExecutionSettings
            {
                filters = new[] { filter }
            };

            var run = new TestRunState
            {
                Id = $"testrun-{DateTime.UtcNow:yyyyMMddHHmmssfff}",
                Mode = mode,
                StartedUtc = DateTime.UtcNow,
                Status = "running"
            };

            var callbacks = new CodexTestCallbacks(run);
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            run.Api = api;
            run.Callbacks = callbacks;

            lock (TestRunLock)
            {
                _currentTestRun = run;
            }

            api.RegisterCallbacks(callbacks);
            api.Execute(settings);

            return Ok(TestRunPayload(run));
        }

        private static Filter BuildTestFilter(Dictionary<string, string> query, string mode)
        {
            var filter = new Filter();

            if (query.TryGetValue("assemblyNames", out var assemblyNames) && !string.IsNullOrWhiteSpace(assemblyNames))
            {
                filter.assemblyNames = SplitCsv(assemblyNames);
            }

            if (query.TryGetValue("testNames", out var testNames) && !string.IsNullOrWhiteSpace(testNames))
            {
                filter.testNames = SplitCsv(testNames);
            }

            if (query.TryGetValue("groupNames", out var groupNames) && !string.IsNullOrWhiteSpace(groupNames))
            {
                filter.groupNames = SplitCsv(groupNames);
            }

            switch (mode)
            {
                case "playmode":
                case "play":
                    filter.testMode = TestMode.PlayMode;
                    break;
                case "editmode":
                case "edit":
                    filter.testMode = TestMode.EditMode;
                    break;
            }

            return filter;
        }

        private static Dictionary<string, object> GetUnityTestStatus(Dictionary<string, string> query)
        {
            lock (TestRunLock)
            {
                if (_currentTestRun == null)
                {
                    return Fail("no_test_run", "No Unity test run has been started");
                }

                var requestedId = Get(query, "runId", string.Empty);
                if (!string.IsNullOrWhiteSpace(requestedId) && !string.Equals(requestedId, _currentTestRun.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return Fail("test_run_not_found", $"Test run '{requestedId}' is not the current run");
                }

                return Ok(TestRunPayload(_currentTestRun));
            }
        }

        private static Dictionary<string, object> GetComponentField(Dictionary<string, string> query)
        {
            if (!TryResolveComponentProperty(query, out var component, out var property, out var error))
            {
                return Fail("component_field_error", error);
            }

            return Ok(new Dictionary<string, object>
            {
                { "gameObject", GameObjectSummary(component.gameObject) },
                { "componentType", component.GetType().FullName },
                { "propertyPath", property.propertyPath },
                { "propertyType", property.propertyType.ToString() },
                { "value", SerializedValue(property) }
            });
        }

        private static Dictionary<string, object> SetComponentField(Dictionary<string, string> query)
        {
            if (!TryResolveComponentProperty(query, out var component, out var property, out var error))
            {
                return Fail("component_field_error", error);
            }

            if (!TryGetJsonValue(query, "valueJson", "value", out var rawValue, out var parsedValue, out error))
            {
                return Fail("invalid_value", error);
            }

            var serialized = property.serializedObject;
            Undo.RecordObject(component, "Unity MCP Set Component Field");
            if (!TryAssignSerializedProperty(property, parsedValue, out error))
            {
                return Fail("unsupported_property_write", error);
            }

            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(component);
            if (!EditorApplication.isPlaying && component.gameObject.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
            }

            return Ok(new Dictionary<string, object>
            {
                { "gameObject", GameObjectSummary(component.gameObject) },
                { "componentType", component.GetType().FullName },
                { "propertyPath", property.propertyPath },
                { "rawValue", rawValue },
                { "value", SerializedValue(property) }
            });
        }

        private static Dictionary<string, object> GetLiveComponentField(Dictionary<string, string> query)
        {
            if (!TryResolveLiveComponentMember(query, out var component, out var memberPath, out var value, out var error))
            {
                return Fail("live_component_field_error", error);
            }

            return Ok(new Dictionary<string, object>
            {
                { "gameObject", GameObjectSummary(component.gameObject) },
                { "componentType", component.GetType().FullName },
                { "memberPath", memberPath },
                { "isPlaying", EditorApplication.isPlaying },
                { "value", RuntimeValue(value) }
            });
        }

        private static Dictionary<string, object> SendKeyboardInput(Dictionary<string, string> query)
        {
            var key = Get(query, "key", string.Empty);
            if (string.IsNullOrWhiteSpace(key) || !Enum.TryParse(key, true, out KeyCode keyCode))
            {
                return Fail("invalid_key", $"Unknown Unity KeyCode '{key}'");
            }

            var eventType = Get(query, "eventType", "press").Trim().ToLowerInvariant();
            var character = Get(query, "character", string.Empty);
            if (!TryPrepareGameViewForInput(out var gameView, out var error))
            {
                return Fail("game_view_unavailable", error);
            }

            var inputSystemInjected = false;
            string inputSystemError = null;
            string inputSystemKey = null;

            try
            {
                inputSystemInjected = TryInjectKeyboardIntoInputSystem(keyCode, eventType, out inputSystemKey, out inputSystemError);
                switch (eventType)
                {
                    case "down":
                        SendGameViewEvent(gameView, BuildKeyEvent(EventType.KeyDown, keyCode, character));
                        break;
                    case "up":
                        SendGameViewEvent(gameView, BuildKeyEvent(EventType.KeyUp, keyCode, character));
                        break;
                    default:
                        SendGameViewEvent(gameView, BuildKeyEvent(EventType.KeyDown, keyCode, character));
                        SendGameViewEvent(gameView, BuildKeyEvent(EventType.KeyUp, keyCode, character));
                        break;
                }
            }
            catch (Exception ex)
            {
                return Fail("game_view_input_error", ex.Message);
            }

            return Ok(new Dictionary<string, object>
            {
                { "key", keyCode.ToString() },
                { "eventType", eventType },
                { "inputSystemInjected", inputSystemInjected },
                { "inputSystemKey", inputSystemKey },
                { "inputSystemError", inputSystemError },
                { "gameView", EditorWindowPayload(gameView, "game") }
            });
        }

        private static Dictionary<string, object> SendMouseInput(Dictionary<string, string> query)
        {
            if (!TryPrepareGameViewForInput(out var gameView, out var error))
            {
                return Fail("game_view_unavailable", error);
            }

            var x = Float(query, "x", float.NaN);
            var y = Float(query, "y", float.NaN);
            if (float.IsNaN(x) || float.IsNaN(y))
            {
                return Fail("mouse_position_required", "x and y are required");
            }

            var button = Int(query, "button", 0);
            var eventType = Get(query, "eventType", "click").Trim().ToLowerInvariant();
            var guiPoint = ScreenToGameViewPoint(gameView, new Vector2(x, y));
            var screenPoint = new Vector2(x, y);
            var inputSystemInjected = false;
            string inputSystemError = null;

            try
            {
                inputSystemInjected = TryInjectMouseIntoInputSystem(screenPoint, button, eventType, out inputSystemError);
                switch (eventType)
                {
                    case "move":
                        SendGameViewEvent(gameView, BuildMouseEvent(EventType.MouseMove, guiPoint, button));
                        break;
                    case "down":
                        SendGameViewEvent(gameView, BuildMouseEvent(EventType.MouseMove, guiPoint, button));
                        SendGameViewEvent(gameView, BuildMouseEvent(EventType.MouseDown, guiPoint, button));
                        break;
                    case "up":
                        SendGameViewEvent(gameView, BuildMouseEvent(EventType.MouseMove, guiPoint, button));
                        SendGameViewEvent(gameView, BuildMouseEvent(EventType.MouseUp, guiPoint, button));
                        break;
                    default:
                        SendGameViewEvent(gameView, BuildMouseEvent(EventType.MouseMove, guiPoint, button));
                        SendGameViewEvent(gameView, BuildMouseEvent(EventType.MouseDown, guiPoint, button));
                        SendGameViewEvent(gameView, BuildMouseEvent(EventType.MouseUp, guiPoint, button));
                        break;
                }
            }
            catch (Exception ex)
            {
                return Fail("game_view_input_error", ex.Message);
            }

            return Ok(new Dictionary<string, object>
            {
                { "eventType", eventType },
                { "button", button },
                { "inputSystemInjected", inputSystemInjected },
                { "inputSystemError", inputSystemError },
                { "screenPosition", Vec2(new Vector2(x, y)) },
                { "gameViewPosition", Vec2(guiPoint) },
                { "gameView", EditorWindowPayload(gameView, "game") }
            });
        }

        private static Dictionary<string, object> ClickUiElement(Dictionary<string, string> query)
        {
            var go = ResolveGameObject(query);
            if (go == null)
            {
                return Fail("not_found", "UI GameObject not found");
            }

            var rectTransform = go.GetComponent<RectTransform>();
            if (rectTransform == null)
            {
                return Fail("not_ui_element", $"GameObject '{go.name}' does not have a RectTransform");
            }

            var screenPoint = RectTransformScreenPoint(rectTransform);
            var mouseQuery = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "x", screenPoint.x.ToString("R", CultureInfo.InvariantCulture) },
                { "y", screenPoint.y.ToString("R", CultureInfo.InvariantCulture) },
                { "button", Get(query, "button", "0") },
                { "eventType", "click" }
            };

            var clickResult = SendMouseInput(mouseQuery);
            if (!IsOk(clickResult))
            {
                return clickResult;
            }

            var data = ObjectDict(clickResult, "data") ?? new Dictionary<string, object>();
            data["gameObject"] = GameObjectSummary(go);
            return Ok(data);
        }

        private static Dictionary<string, object> WaitForObjectCondition(Dictionary<string, string> query)
        {
            var shouldExist = Bool(query, "exists", true);
            var go = ResolveGameObject(query);
            var exists = go != null;
            if (exists == shouldExist)
            {
                return Ok(new Dictionary<string, object>
                {
                    { "exists", exists },
                    { "gameObject", go == null ? null : GameObjectSummary(go) }
                });
            }

            return Fail("wait_pending", "Object existence condition has not been met yet");
        }

        private static Dictionary<string, object> WaitForSceneCondition(Dictionary<string, string> query)
        {
            var scene = SceneManager.GetActiveScene();
            var expectedName = Get(query, "sceneName", string.Empty);
            var expectedPath = Get(query, "scenePath", string.Empty);
            var match = true;

            if (!string.IsNullOrWhiteSpace(expectedName))
            {
                match &= string.Equals(scene.name, expectedName, StringComparison.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrWhiteSpace(expectedPath))
            {
                match &= string.Equals(scene.path, expectedPath, StringComparison.OrdinalIgnoreCase);
            }

            if (match)
            {
                return Ok(new Dictionary<string, object>
                {
                    { "scene", scene.name },
                    { "path", scene.path },
                    { "isLoaded", scene.isLoaded }
                });
            }

            return Fail("wait_pending", $"Active scene is '{scene.name}'");
        }

        private static Dictionary<string, object> WaitForPlayModeCondition(Dictionary<string, string> query)
        {
            var expectedPlaying = Bool(query, "isPlaying", true);
            var expectedPaused = query.ContainsKey("isPaused") ? Bool(query, "isPaused", false) : EditorApplication.isPaused;

            if (EditorApplication.isPlaying == expectedPlaying &&
                (!query.ContainsKey("isPaused") || EditorApplication.isPaused == expectedPaused))
            {
                return Ok(PlayStatePayload(false));
            }

            return Fail("wait_pending", "Play mode state has not reached the requested value yet");
        }

        private static Dictionary<string, object> WaitForLogCondition(Dictionary<string, string> query)
        {
            var text = Get(query, "text", string.Empty);
            if (string.IsNullOrWhiteSpace(text))
            {
                return Fail("text_required", "text is required");
            }

            var type = Get(query, "type", string.Empty);
            var sinceSeconds = Float(query, "sinceSeconds", 60f);
            var cutoff = DateTime.UtcNow.AddSeconds(-Math.Max(0.1f, sinceSeconds));
            RuntimeLogEntry match = null;

            lock (RecentLogsLock)
            {
                for (var i = RecentLogs.Count - 1; i >= 0; i--)
                {
                    var entry = RecentLogs[i];
                    if (entry.TimestampUtc < cutoff)
                    {
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(type) && !string.Equals(entry.Type, type, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (entry.Message.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        match = entry;
                        break;
                    }
                }
            }

            if (match != null)
            {
                return Ok(RuntimeLogPayload(match));
            }

            return Fail("wait_pending", $"Log containing '{text}' has not been observed yet");
        }

        private static Dictionary<string, object> WaitForComponentFieldCondition(Dictionary<string, string> query)
        {
            if (!TryResolveComponentProperty(query, out var component, out var property, out var error))
            {
                return Fail("component_field_error", error);
            }

            if (!TryGetJsonValue(query, "expectedJson", "expected", out var rawExpected, out var expected, out error))
            {
                return Fail("invalid_expected", error);
            }

            var actual = SerializedValue(property);
            var comparison = Get(query, "comparison", "equals").Trim().ToLowerInvariant();
            if (MatchesComparison(actual, expected, comparison))
            {
                return Ok(new Dictionary<string, object>
                {
                    { "gameObject", GameObjectSummary(component.gameObject) },
                    { "componentType", component.GetType().FullName },
                    { "propertyPath", property.propertyPath },
                    { "comparison", comparison },
                    { "expected", expected },
                    { "rawExpected", rawExpected },
                    { "actual", actual }
                });
            }

            return Fail("wait_pending", $"Component field '{property.propertyPath}' has not matched comparison '{comparison}' yet");
        }

        private static Dictionary<string, object> WaitForLiveComponentFieldCondition(Dictionary<string, string> query)
        {
            if (!TryResolveLiveComponentMember(query, out var component, out var memberPath, out var actual, out var error))
            {
                return Fail("live_component_field_error", error);
            }

            if (!TryGetJsonValue(query, "expectedJson", "expected", out var rawExpected, out var expected, out error))
            {
                return Fail("invalid_expected", error);
            }

            var comparison = Get(query, "comparison", "equals").Trim().ToLowerInvariant();
            var runtimeActual = RuntimeValue(actual);
            if (MatchesComparison(runtimeActual, expected, comparison))
            {
                return Ok(new Dictionary<string, object>
                {
                    { "gameObject", GameObjectSummary(component.gameObject) },
                    { "componentType", component.GetType().FullName },
                    { "memberPath", memberPath },
                    { "comparison", comparison },
                    { "expectedRaw", rawExpected },
                    { "value", runtimeActual }
                });
            }

            return Fail("wait_pending", $"Runtime field '{memberPath}' has not matched comparison '{comparison}' yet");
        }

        private static Dictionary<string, object> WaitForTestsCondition(Dictionary<string, string> query)
        {
            lock (TestRunLock)
            {
                if (_currentTestRun == null)
                {
                    return Fail("no_test_run", "No Unity test run has been started");
                }

                if (!_currentTestRun.IsComplete)
                {
                    return Fail("wait_pending", $"Unity test run '{_currentTestRun.Id}' is still running");
                }

                if (Bool(query, "requireSuccess", true) && _currentTestRun.FailedCount > 0)
                {
                    return Fail("tests_failed", $"{_currentTestRun.FailedCount} Unity tests failed");
                }

                return Ok(TestRunPayload(_currentTestRun));
            }
        }

        private static Dictionary<string, object> ReadConsole(Dictionary<string, string> query)
        {
            var count = Int(query, "count", 50);
            return ReadConsoleEntries(Math.Max(0, GetConsoleEntryCount() - count), int.MaxValue);
        }

        private static Dictionary<string, object> CreateConsoleCheckpoint(Dictionary<string, string> query)
        {
            var id = Get(query, "id", string.Empty);
            if (string.IsNullOrWhiteSpace(id))
            {
                id = $"console-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            }

            var checkpoint = new ConsoleCheckpoint
            {
                Id = id,
                EntryIndex = GetConsoleEntryCount(),
                CreatedUtc = DateTime.UtcNow
            };
            ConsoleCheckpoints[id] = checkpoint;

            return Ok(new Dictionary<string, object>
            {
                { "id", checkpoint.Id },
                { "entryIndex", checkpoint.EntryIndex },
                { "createdUtc", checkpoint.CreatedUtc.ToString("o", CultureInfo.InvariantCulture) }
            });
        }

        private static Dictionary<string, object> ReadConsoleSinceCheckpoint(Dictionary<string, string> query)
        {
            var id = Get(query, "id", string.Empty);
            if (string.IsNullOrWhiteSpace(id))
            {
                return Fail("checkpoint_required", "id is required");
            }

            if (!ConsoleCheckpoints.TryGetValue(id, out var checkpoint))
            {
                return Fail("checkpoint_not_found", $"Console checkpoint '{id}' was not found");
            }

            return ReadConsoleEntries(checkpoint.EntryIndex, int.MaxValue);
        }

        private static Dictionary<string, object> ClearConsole()
        {
            try
            {
                var logEntriesType = Type.GetType("UnityEditor.LogEntries,UnityEditor");
                if (logEntriesType == null)
                {
                    return Fail("console_unavailable", "Unity LogEntries reflection API is unavailable in this Unity version");
                }

                var clear = logEntriesType.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (clear == null)
                {
                    return Fail("console_clear_unavailable", "Unity console clear API is unavailable in this Unity version");
                }

                clear.Invoke(null, null);
                return Ok(new Dictionary<string, object> { { "cleared", true } });
            }
            catch (Exception ex)
            {
                return Fail("console_clear_error", ex.Message);
            }
        }

        private static int GetConsoleEntryCount()
        {
            try
            {
                var logEntriesType = Type.GetType("UnityEditor.LogEntries,UnityEditor");
                var getCount = logEntriesType == null
                    ? null
                    : logEntriesType.GetMethod("GetCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                return getCount == null ? 0 : (int)getCount.Invoke(null, null);
            }
            catch
            {
                return 0;
            }
        }

        private static Dictionary<string, object> ReadConsoleEntries(int fromIndex, int maxCount)
        {
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
                var from = Math.Max(0, fromIndex);
                var toExclusive = Math.Min(total, from + Math.Max(0, maxCount));
                var entry = Activator.CreateInstance(logEntryType);

                start?.Invoke(null, null);
                for (var i = from; i < toExclusive; i++)
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
                { "fromIndex", fromIndex },
                { "items", entries }
            });
        }

        private static Dictionary<string, object> CaptureEditorScreenshot(Dictionary<string, string> query)
        {
            var target = Get(query, "target", "active_window").Trim().ToLowerInvariant();
            var includeImage = Bool(query, "includeImage", false);
            var maxResolution = Mathf.Clamp(Int(query, "maxResolution", 1400), 128, 4096);

            EditorWindow window;
            switch (target)
            {
                case "scene_view":
                case "scene":
                    window = SceneView.lastActiveSceneView;
                    break;
                case "game_view":
                case "game":
                    window = GetGameViewWindow();
                    break;
                case "active_window":
                    window = EditorWindow.focusedWindow ?? EditorWindow.mouseOverWindow;
                    break;
                default:
                    window = ResolveEditorWindowByToken(target);
                    break;
            }

            if (window == null)
            {
                return Fail("window_not_found", $"No editor window available for target '{target}'");
            }

            FocusWindow(window);
            window.Repaint();
            InternalEditorUtilityRepaintAllViews();

            var bytes = TryCaptureEditorWindow(window, maxResolution, out var width, out var height, out var error);
            if (bytes == null)
            {
                return Fail("editor_capture_failed", error);
            }

            var payload = SaveScreenshotPayload(bytes, $"editor_{target}", width, height, includeImage);
            payload["target"] = target;
            payload["windowTitle"] = window.titleContent == null ? string.Empty : window.titleContent.text;
            payload["windowType"] = window.GetType().FullName;
            return Ok(payload);
        }

        private static Dictionary<string, object> CaptureFullEditorScreenshot(Dictionary<string, string> query)
        {
            var includeImage = Bool(query, "includeImage", false);
            var maxResolution = Mathf.Clamp(Int(query, "maxResolution", 1800), 256, 4096);
            var rect = ResolveMainEditorWindowRect();
            if (!rect.HasValue)
            {
                return Fail("main_window_unavailable", "Could not resolve Unity main window bounds");
            }

            InternalEditorUtilityRepaintAllViews();
            var bytes = TryCaptureScreenRect(rect.Value, maxResolution, out var width, out var height, out var error);
            if (bytes == null)
            {
                return Fail("editor_capture_failed", error);
            }

            var payload = SaveScreenshotPayload(bytes, "editor_full", width, height, includeImage);
            payload["target"] = "full_editor";
            payload["windowRect"] = new Dictionary<string, object>
            {
                { "x", rect.Value.x },
                { "y", rect.Value.y },
                { "width", rect.Value.width },
                { "height", rect.Value.height }
            };
            return Ok(payload);
        }

        private static Dictionary<string, object> FocusEditorWindow(Dictionary<string, string> query)
        {
            var target = Get(query, "target", string.Empty);
            if (string.IsNullOrWhiteSpace(target))
            {
                return Fail("target_required", "target is required");
            }

            var window = ResolveOrOpenEditorWindow(target);
            if (window == null)
            {
                return Fail("window_not_found", $"Editor window '{target}' could not be resolved");
            }

            FocusWindow(window);
            return Ok(EditorWindowPayload(window, target));
        }

        private static Dictionary<string, object> SelectSceneObject(Dictionary<string, string> query)
        {
            var go = ResolveGameObject(query);
            if (go == null)
            {
                return Fail("not_found", "GameObject not found");
            }

            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
            return Ok(new Dictionary<string, object>
            {
                { "selectionType", "scene_object" },
                { "gameObject", GameObjectDetails(go, false) }
            });
        }

        private static Dictionary<string, object> SelectAsset(Dictionary<string, string> query)
        {
            var asset = ResolveAsset(query, out var assetPath);
            if (asset == null)
            {
                return Fail("asset_not_found", "Asset not found");
            }

            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
            return Ok(AssetPayload(asset, assetPath, "selected"));
        }

        private static Dictionary<string, object> OpenAsset(Dictionary<string, string> query)
        {
            var asset = ResolveAsset(query, out var assetPath);
            if (asset == null)
            {
                return Fail("asset_not_found", "Asset not found");
            }

            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
            var opened = AssetDatabase.OpenAsset(asset);
            return Ok(new Dictionary<string, object>
            {
                { "opened", opened },
                { "asset", AssetPayload(asset, assetPath, "opened") }
            });
        }

        private static Dictionary<string, object> RevealAssetInProject(Dictionary<string, string> query)
        {
            var asset = ResolveAsset(query, out var assetPath);
            if (asset == null)
            {
                return Fail("asset_not_found", "Asset not found");
            }

            Selection.activeObject = asset;
            EditorUtility.FocusProjectWindow();
            EditorGUIUtility.PingObject(asset);
            return Ok(AssetPayload(asset, assetPath, "revealed"));
        }

        private static Dictionary<string, object> SearchAssets(Dictionary<string, string> query)
        {
            var filter = Get(query, "filter", string.Empty);
            var inFolders = Get(query, "inFolders", string.Empty);
            var limit = Mathf.Clamp(Int(query, "limit", 50), 1, 500);
            var folders = string.IsNullOrWhiteSpace(inFolders) ? null : SplitCsv(inFolders);
            var guids = folders == null || folders.Length == 0
                ? AssetDatabase.FindAssets(filter)
                : AssetDatabase.FindAssets(filter, folders);

            var items = new List<object>();
            for (var i = 0; i < guids.Length && items.Count < limit; i++)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var asset = AssetDatabase.LoadAssetAtPath<Object>(path);
                if (asset == null)
                {
                    continue;
                }

                var payload = AssetPayload(asset, path, "found");
                payload["guid"] = guids[i];
                items.Add(payload);
            }

            return Ok(new Dictionary<string, object>
            {
                { "filter", filter },
                { "count", items.Count },
                { "items", items }
            });
        }

        private static Dictionary<string, object> CreateScriptableObjectAsset(Dictionary<string, string> query)
        {
            var typeName = Get(query, "typeName", string.Empty);
            if (!TryResolveScriptableObjectType(typeName, out var scriptableObjectType, out var error))
            {
                return Fail("scriptable_object_type_error", error);
            }

            var assetPath = NormalizeAssetPath(Get(query, "assetPath", string.Empty));
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return Fail("asset_path_required", "assetPath is required");
            }

            if (!assetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
            {
                return Fail("invalid_asset_path", "ScriptableObject assets must use a .asset path");
            }

            if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(assetPath, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                return Fail("invalid_asset_path", "ScriptableObject assets must live under Assets/");
            }

            var overwrite = Bool(query, "overwrite", false);
            if (AssetDatabase.LoadAssetAtPath<Object>(assetPath) != null)
            {
                if (!overwrite)
                {
                    return Fail("asset_exists", $"Asset already exists at '{assetPath}'");
                }

                AssetDatabase.DeleteAsset(assetPath);
            }

            EnsureAssetFolder(assetPath);
            var asset = ScriptableObject.CreateInstance(scriptableObjectType);
            AssetDatabase.CreateAsset(asset, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return Ok(ScriptableObjectPayload(asset, assetPath, true));
        }

        private static Dictionary<string, object> InspectScriptableObjectAsset(Dictionary<string, string> query)
        {
            if (!TryResolveScriptableObjectAsset(query, out var asset, out var assetPath, out var error))
            {
                return Fail("scriptable_object_asset_error", error);
            }

            var includeProperties = Bool(query, "includeProperties", true);
            return Ok(ScriptableObjectPayload(asset, assetPath, includeProperties));
        }

        private static Dictionary<string, object> SetScriptableObjectField(Dictionary<string, string> query)
        {
            if (!TryResolveScriptableObjectAsset(query, out var asset, out var assetPath, out var error))
            {
                return Fail("scriptable_object_asset_error", error);
            }

            var propertyPath = Get(query, "propertyPath", string.Empty);
            if (string.IsNullOrWhiteSpace(propertyPath))
            {
                return Fail("property_path_required", "propertyPath is required");
            }

            if (!TryGetJsonValue(query, "valueJson", "value", out var rawValue, out var parsedValue, out error))
            {
                return Fail("invalid_value", error);
            }

            var serialized = new SerializedObject(asset);
            var property = serialized.FindProperty(propertyPath);
            if (property == null)
            {
                return Fail("property_not_found", $"Property '{propertyPath}' was not found on asset {asset.GetType().FullName}");
            }

            Undo.RecordObject(asset, "Unity MCP Set ScriptableObject Field");
            if (!TryAssignSerializedProperty(property, parsedValue, out error))
            {
                return Fail("unsupported_property_write", error);
            }

            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();

            return Ok(new Dictionary<string, object>
            {
                { "asset", AssetPayload(asset, assetPath, "updated") },
                { "assetType", asset.GetType().FullName },
                { "propertyPath", property.propertyPath },
                { "rawValue", rawValue },
                { "value", SerializedValue(property) }
            });
        }

        private static Dictionary<string, object> SaveEditorSession(Dictionary<string, string> query)
        {
            var id = Get(query, "id", string.Empty);
            if (string.IsNullOrWhiteSpace(id))
            {
                id = $"session-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            }

            var state = CaptureEditorSession(id);
            EditorSessions[id] = state;
            return Ok(EditorSessionPayload(state));
        }

        private static Dictionary<string, object> RestoreEditorSession(Dictionary<string, string> query)
        {
            var id = Get(query, "id", string.Empty);
            if (string.IsNullOrWhiteSpace(id))
            {
                return Fail("session_required", "id is required");
            }

            if (!EditorSessions.TryGetValue(id, out var state))
            {
                return Fail("session_not_found", $"Editor session '{id}' was not found");
            }

            if (!string.IsNullOrWhiteSpace(state.FocusedWindowTarget))
            {
                var window = ResolveOrOpenEditorWindow(state.FocusedWindowTarget);
                if (window != null)
                {
                    FocusWindow(window);
                }
            }

            if (!string.IsNullOrWhiteSpace(state.SelectedAssetPath))
            {
                var asset = AssetDatabase.LoadAssetAtPath<Object>(state.SelectedAssetPath);
                if (asset != null)
                {
                    Selection.activeObject = asset;
                    EditorGUIUtility.PingObject(asset);
                }
            }
            else if (!string.IsNullOrWhiteSpace(state.SelectedObjectPath))
            {
                var go = ResolveGameObject(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "path", state.SelectedObjectPath }
                });
                if (go != null)
                {
                    Selection.activeGameObject = go;
                    EditorGUIUtility.PingObject(go);
                }
            }

            return Ok(EditorSessionPayload(CaptureEditorSession(id)));
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

                var dir = GetScreenshotsDirectory();
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

        private static byte[] TryCaptureEditorWindow(EditorWindow window, int maxResolution, out int width, out int height, out string error)
        {
            return TryCaptureScreenRect(window.position, maxResolution, out width, out height, out error);
        }

        private static byte[] TryCaptureScreenRect(Rect rect, int maxResolution, out int width, out int height, out string error)
        {
            width = 0;
            height = 0;
            error = null;

            var readScreenPixel = ResolveReadScreenPixelMethod();
            if (readScreenPixel == null)
            {
                error = "Unity internal screen pixel capture API is unavailable in this version";
                return null;
            }

            var captureWidth = Mathf.Max(16, Mathf.RoundToInt(rect.width));
            var captureHeight = Mathf.Max(16, Mathf.RoundToInt(rect.height));
            var scale = Mathf.Min(1f, maxResolution / (float)Mathf.Max(captureWidth, captureHeight));
            width = Mathf.Max(16, Mathf.RoundToInt(captureWidth * scale));
            height = Mathf.Max(16, Mathf.RoundToInt(captureHeight * scale));

            try
            {
                var raw = readScreenPixel.Invoke(null, new object[]
                {
                    new Vector2(rect.x, rect.y),
                    captureWidth,
                    captureHeight
                }) as Color[];

                if (raw == null || raw.Length == 0)
                {
                    error = "Unity returned no pixels for editor capture";
                    return null;
                }

                var texture = new Texture2D(captureWidth, captureHeight, TextureFormat.RGBA32, false);
                texture.SetPixels(raw);
                texture.Apply();

                Texture2D finalTexture = texture;
                if (width != captureWidth || height != captureHeight)
                {
                    finalTexture = ScaleTexture(texture, width, height);
                    Object.DestroyImmediate(texture);
                }
                else
                {
                    width = captureWidth;
                    height = captureHeight;
                }

                var bytes = finalTexture.EncodeToPNG();
                Object.DestroyImmediate(finalTexture);
                return bytes;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private static MethodInfo ResolveReadScreenPixelMethod()
        {
            var type = Type.GetType("UnityEditorInternal.InternalEditorUtility,UnityEditor");
            return type == null
                ? null
                : type.GetMethod("ReadScreenPixel", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector2), typeof(int), typeof(int) }, null);
        }

        private static Texture2D ScaleTexture(Texture2D source, int width, int height)
        {
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;

            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var scaled = new Texture2D(width, height, TextureFormat.RGBA32, false);
                scaled.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                scaled.Apply();
                return scaled;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static Dictionary<string, object> SaveScreenshotPayload(byte[] bytes, string prefix, int width, int height, bool includeImage)
        {
            var dir = GetScreenshotsDirectory();
            Directory.CreateDirectory(dir);
            var fileName = $"{prefix}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.png";
            var filePath = Path.GetFullPath(Path.Combine(dir, fileName));
            File.WriteAllBytes(filePath, bytes);

            var payload = new Dictionary<string, object>
            {
                { "path", filePath },
                { "width", width },
                { "height", height }
            };

            if (includeImage)
            {
                payload["imageBase64"] = Convert.ToBase64String(bytes);
            }

            return payload;
        }

        private static string GetScreenshotsDirectory()
        {
            return Path.Combine(Application.dataPath, "..", "Library", "UnityMcp", "Screenshots");
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

        private static Dictionary<string, object> AssetPayload(Object asset, string assetPath, string action)
        {
            return new Dictionary<string, object>
            {
                { "action", action },
                { "name", asset.name },
                { "type", asset.GetType().FullName },
                { "assetPath", assetPath },
                { "instanceId", asset.GetInstanceID() }
            };
        }

        private static EditorSessionState CaptureEditorSession(string id)
        {
            var focusedWindow = EditorWindow.focusedWindow ?? EditorWindow.mouseOverWindow;
            var selectedObject = Selection.activeGameObject;
            var selectedAsset = selectedObject == null ? Selection.activeObject : null;

            return new EditorSessionState
            {
                Id = id,
                CapturedUtc = DateTime.UtcNow,
                FocusedWindowTarget = FocusTargetForWindow(focusedWindow),
                SelectedObjectPath = selectedObject == null ? string.Empty : GetPath(selectedObject),
                SelectedAssetPath = selectedAsset == null ? string.Empty : AssetDatabase.GetAssetPath(selectedAsset)
            };
        }

        private static Dictionary<string, object> EditorSessionPayload(EditorSessionState state)
        {
            return new Dictionary<string, object>
            {
                { "id", state.Id },
                { "capturedUtc", state.CapturedUtc.ToString("o", CultureInfo.InvariantCulture) },
                { "focusedWindowTarget", state.FocusedWindowTarget },
                { "selectedObjectPath", state.SelectedObjectPath },
                { "selectedAssetPath", state.SelectedAssetPath }
            };
        }

        private static string FocusTargetForWindow(EditorWindow window)
        {
            if (window == null)
            {
                return string.Empty;
            }

            var typeName = window.GetType().FullName ?? string.Empty;
            if (typeName.IndexOf("InspectorWindow", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "inspector";
            }
            if (typeName.IndexOf("ProjectBrowser", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "project";
            }
            if (typeName.IndexOf("ConsoleWindow", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "console";
            }
            if (typeName.IndexOf("SceneHierarchyWindow", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "hierarchy";
            }
            if (window is SceneView)
            {
                return "scene";
            }
            if (typeName.IndexOf("GameView", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "game";
            }

            return string.Empty;
        }

        private static Object ResolveAsset(Dictionary<string, string> query, out string assetPath)
        {
            assetPath = Get(query, "assetPath", string.Empty);
            if (!string.IsNullOrWhiteSpace(assetPath))
            {
                var direct = AssetDatabase.LoadAssetAtPath<Object>(assetPath);
                if (direct != null)
                {
                    return direct;
                }
            }

            var guid = Get(query, "guid", string.Empty);
            if (!string.IsNullOrWhiteSpace(guid))
            {
                var pathFromGuid = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrWhiteSpace(pathFromGuid))
                {
                    assetPath = pathFromGuid;
                    var guidAsset = AssetDatabase.LoadAssetAtPath<Object>(assetPath);
                    if (guidAsset != null)
                    {
                        return guidAsset;
                    }
                }
            }

            return null;
        }

        private static bool TryResolveScriptableObjectAsset(
            Dictionary<string, string> query,
            out ScriptableObject asset,
            out string assetPath,
            out string error)
        {
            asset = null;
            error = null;
            var resolved = ResolveAsset(query, out assetPath);
            if (resolved == null)
            {
                error = "Asset not found";
                return false;
            }

            asset = resolved as ScriptableObject;
            if (asset == null)
            {
                error = $"Asset '{assetPath}' is not a ScriptableObject";
                return false;
            }

            return true;
        }

        private static string NormalizeAssetPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return string.Empty;
            }

            assetPath = assetPath.Trim().Replace('\\', '/');
            return assetPath;
        }

        private static void EnsureAssetFolder(string assetPath)
        {
            var folder = Path.GetDirectoryName(assetPath.Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            folder = folder.Replace('\\', '/');
            if (AssetDatabase.IsValidFolder(folder))
            {
                return;
            }

            var parts = folder.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !string.Equals(parts[0], "Assets", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var current = "Assets";
            for (var i = 1; i < parts.Length; i++)
            {
                var next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }
                current = next;
            }
        }

        private static Rect? ResolveMainEditorWindowRect()
        {
            var editorGuiUtility = typeof(EditorGUIUtility);
            var method = editorGuiUtility.GetMethod("GetMainWindowPosition", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method != null)
            {
                try
                {
                    return (Rect)method.Invoke(null, null);
                }
                catch
                {
                    // Fall back below.
                }
            }

            var focused = EditorWindow.focusedWindow ?? EditorWindow.mouseOverWindow;
            return focused == null ? (Rect?)null : focused.position;
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

        private static Dictionary<string, object> ScriptableObjectPayload(ScriptableObject asset, string assetPath, bool includeProperties)
        {
            var payload = AssetPayload(asset, assetPath, "scriptable_object");
            payload["assetType"] = asset.GetType().FullName;
            if (includeProperties)
            {
                payload["properties"] = SerializedProperties(asset, 128);
            }

            return payload;
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

        private static object RuntimeValue(object value)
        {
            if (value == null)
            {
                return null;
            }

            switch (value)
            {
                case bool _:
                case byte _:
                case sbyte _:
                case short _:
                case ushort _:
                case int _:
                case uint _:
                case long _:
                case ulong _:
                case float _:
                case double _:
                case decimal _:
                case string _:
                    return value;
                case Vector2 vector2:
                    return Vec2(vector2);
                case Vector3 vector3:
                    return Vec3(vector3);
                case Vector4 vector4:
                    return Vec4(vector4);
                case Quaternion quaternion:
                    return QuaternionValue(quaternion);
                case Color color:
                    return Vec4(new Vector4(color.r, color.g, color.b, color.a));
                case Rect rect:
                    return new Dictionary<string, object>
                    {
                        { "x", rect.x },
                        { "y", rect.y },
                        { "width", rect.width },
                        { "height", rect.height }
                    };
                case Bounds bounds:
                    return new Dictionary<string, object>
                    {
                        { "center", Vec3(bounds.center) },
                        { "size", Vec3(bounds.size) }
                    };
                case Enum enumValue:
                    return enumValue.ToString();
                case Object unityObject:
                    return unityObject == null
                        ? null
                        : new Dictionary<string, object>
                        {
                            { "name", unityObject.name },
                            { "type", unityObject.GetType().FullName },
                            { "instanceId", unityObject.GetInstanceID() },
                            { "assetPath", AssetDatabase.GetAssetPath(unityObject) }
                        };
                case IList list:
                {
                    var items = new List<object>();
                    foreach (var item in list)
                    {
                        items.Add(RuntimeValue(item));
                    }
                    return items;
                }
                default:
                    return Convert.ToString(value, CultureInfo.InvariantCulture);
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

        private static SceneSnapshot CaptureSceneSnapshot(string id)
        {
            var scene = SceneManager.GetActiveScene();
            var objects = new Dictionary<string, SnapshotObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var go in SceneGameObjects(true))
            {
                var path = GetPath(go);
                objects[path] = new SnapshotObject
                {
                    Id = go.GetInstanceID(),
                    Name = go.name,
                    Path = path,
                    ActiveSelf = go.activeSelf,
                    ActiveInHierarchy = go.activeInHierarchy,
                    Tag = SafeTag(go),
                    Layer = LayerMask.LayerToName(go.layer),
                    TransformSignature = TransformSignature(go.transform),
                    ComponentsSignature = ComponentsSignature(go)
                };
            }

            return new SceneSnapshot
            {
                Id = id,
                CapturedUtc = DateTime.UtcNow,
                SceneName = scene.name,
                ScenePath = scene.path,
                IsDirty = scene.isDirty,
                Objects = objects
            };
        }

        private static Dictionary<string, object> SnapshotSummary(SceneSnapshot snapshot)
        {
            return new Dictionary<string, object>
            {
                { "id", snapshot.Id },
                { "capturedUtc", snapshot.CapturedUtc.ToString("o", CultureInfo.InvariantCulture) },
                { "scene", snapshot.SceneName },
                { "path", snapshot.ScenePath },
                { "isDirty", snapshot.IsDirty },
                { "objectCount", snapshot.Objects.Count }
            };
        }

        private static Dictionary<string, object> BuildSnapshotDiff(SceneSnapshot before, SceneSnapshot after)
        {
            var added = new List<object>();
            var removed = new List<object>();
            var changed = new List<object>();

            foreach (var entry in after.Objects)
            {
                if (!before.Objects.TryGetValue(entry.Key, out var beforeObject))
                {
                    added.Add(SnapshotObjectPayload(entry.Value));
                    continue;
                }

                var changes = SnapshotObjectChanges(beforeObject, entry.Value);
                if (changes.Count > 0)
                {
                    changed.Add(new Dictionary<string, object>
                    {
                        { "path", entry.Key },
                        { "before", SnapshotObjectPayload(beforeObject) },
                        { "after", SnapshotObjectPayload(entry.Value) },
                        { "changes", changes }
                    });
                }
            }

            foreach (var entry in before.Objects)
            {
                if (!after.Objects.ContainsKey(entry.Key))
                {
                    removed.Add(SnapshotObjectPayload(entry.Value));
                }
            }

            return new Dictionary<string, object>
            {
                { "before", SnapshotSummary(before) },
                { "after", SnapshotSummary(after) },
                { "addedCount", added.Count },
                { "removedCount", removed.Count },
                { "changedCount", changed.Count },
                { "added", added },
                { "removed", removed },
                { "changed", changed }
            };
        }

        private static Dictionary<string, object> SnapshotObjectPayload(SnapshotObject item)
        {
            return new Dictionary<string, object>
            {
                { "id", item.Id },
                { "name", item.Name },
                { "path", item.Path },
                { "activeSelf", item.ActiveSelf },
                { "activeInHierarchy", item.ActiveInHierarchy },
                { "tag", item.Tag },
                { "layer", item.Layer },
                { "transform", item.TransformSignature },
                { "components", item.ComponentsSignature }
            };
        }

        private static List<object> SnapshotObjectChanges(SnapshotObject before, SnapshotObject after)
        {
            var changes = new List<object>();
            AddSnapshotChange(changes, "activeSelf", before.ActiveSelf, after.ActiveSelf);
            AddSnapshotChange(changes, "activeInHierarchy", before.ActiveInHierarchy, after.ActiveInHierarchy);
            AddSnapshotChange(changes, "tag", before.Tag, after.Tag);
            AddSnapshotChange(changes, "layer", before.Layer, after.Layer);
            AddSnapshotChange(changes, "transform", before.TransformSignature, after.TransformSignature);
            AddSnapshotChange(changes, "components", before.ComponentsSignature, after.ComponentsSignature);
            return changes;
        }

        private static void AddSnapshotChange(List<object> changes, string field, object before, object after)
        {
            var beforeText = MiniJson.Serialize(before);
            var afterText = MiniJson.Serialize(after);
            if (beforeText == afterText)
            {
                return;
            }

            changes.Add(new Dictionary<string, object>
            {
                { "field", field },
                { "before", before },
                { "after", after }
            });
        }

        private static List<object> ComponentsSignature(GameObject go)
        {
            var components = new List<object>();
            foreach (var component in go.GetComponents<Component>())
            {
                components.Add(component == null ? "<missing>" : component.GetType().FullName);
            }
            return components;
        }

        private static Dictionary<string, object> TransformSignature(Transform transform)
        {
            return new Dictionary<string, object>
            {
                { "position", Vec3(transform.position) },
                { "rotation", Vec3(transform.eulerAngles) },
                { "scale", Vec3(transform.localScale) }
            };
        }

        private static Dictionary<string, object> ExecuteSafeBatchCommand(Dictionary<string, object> command)
        {
            var tool = GetObjectString(command, "tool");
            var parameters = ObjectDict(command, "params");
            if (parameters == null)
            {
                parameters = ObjectDict(command, "args") ?? new Dictionary<string, object>();
            }

            var query = QueryFromObject(parameters);
            switch (tool)
            {
                case "create_gameobject":
                case "create-gameobject":
                    return CreateGameObject(query);
                case "delete_gameobject":
                case "delete-gameobject":
                    return DeleteGameObject(query);
                case "duplicate_gameobject":
                case "duplicate-gameobject":
                    return DuplicateGameObject(query);
                case "set_transform":
                case "set-transform":
                    return SetTransform(query);
                case "add_component":
                case "add-component":
                    return AddComponentToGameObject(query);
                case "remove_component":
                case "remove-component":
                    return RemoveComponentFromGameObject(query);
                default:
                    return Fail("unsupported_batch_tool", $"Safe batch does not support tool '{tool}'");
            }
        }

        private static bool IsOk(object result)
        {
            var dictionary = result as Dictionary<string, object>;
            return dictionary != null && dictionary.TryGetValue("ok", out var ok) && ok is bool value && value;
        }

        private static Dictionary<string, object> ObjectDict(Dictionary<string, object> source, string key)
        {
            return source.TryGetValue(key, out var value) ? value as Dictionary<string, object> : null;
        }

        private static string GetObjectString(Dictionary<string, object> source, string key)
        {
            return source.TryGetValue(key, out var value) && value != null ? value.ToString() : string.Empty;
        }

        private static Dictionary<string, string> QueryFromObject(Dictionary<string, object> parameters)
        {
            var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in parameters)
            {
                if (entry.Value == null)
                {
                    continue;
                }

                if (entry.Value is IList list && !(entry.Value is string))
                {
                    var parts = new List<string>();
                    foreach (var item in list)
                    {
                        parts.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
                    }
                    query[NormalizeQueryKey(entry.Key)] = string.Join(",", parts.ToArray());
                }
                else if (entry.Value is bool boolean)
                {
                    query[NormalizeQueryKey(entry.Key)] = boolean ? "true" : "false";
                }
                else
                {
                    query[NormalizeQueryKey(entry.Key)] = Convert.ToString(entry.Value, CultureInfo.InvariantCulture);
                }
            }

            return query;
        }

        private static string NormalizeQueryKey(string key)
        {
            switch (key)
            {
                case "instance_id":
                    return "id";
                case "primitive_type":
                    return "primitiveType";
                case "parent_id":
                    return "parentId";
                case "parent_name":
                    return "parentName";
                case "parent_path":
                    return "parentPath";
                case "new_name":
                    return "newName";
                case "component_type":
                    return "componentType";
                case "allow_multiple":
                    return "allowMultiple";
                case "remove_all":
                    return "removeAll";
                default:
                    return key;
            }
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

        private static bool TryResolveComponentType(string componentTypeName, out Type componentType, out string error)
        {
            componentType = null;
            error = null;

            if (string.IsNullOrWhiteSpace(componentTypeName))
            {
                error = "componentType is required";
                return false;
            }

            var direct = Type.GetType(componentTypeName, false);
            if (direct != null)
            {
                return ValidateComponentType(direct, out componentType, out error);
            }

            var matches = new List<Type>();
            var exactFullNameMatches = new List<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var type in GetAssemblyTypes(assembly))
                {
                    if (type == null || !typeof(Component).IsAssignableFrom(type) || type.IsAbstract)
                    {
                        continue;
                    }

                    if (string.Equals(type.FullName, componentTypeName, StringComparison.OrdinalIgnoreCase))
                    {
                        exactFullNameMatches.Add(type);
                    }
                    else if (string.Equals(type.Name, componentTypeName, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(type);
                    }
                }
            }

            if (exactFullNameMatches.Count == 1)
            {
                componentType = exactFullNameMatches[0];
                return true;
            }

            if (exactFullNameMatches.Count > 1)
            {
                error = $"Ambiguous component type '{componentTypeName}': {TypeList(exactFullNameMatches)}";
                return false;
            }

            if (matches.Count == 1)
            {
                componentType = matches[0];
                return true;
            }

            var unityEngineName = $"UnityEngine.{componentTypeName}";
            Type unityEngineMatch = null;
            foreach (var match in matches)
            {
                if (string.Equals(match.FullName, unityEngineName, StringComparison.OrdinalIgnoreCase))
                {
                    unityEngineMatch = match;
                    break;
                }
            }

            if (unityEngineMatch != null)
            {
                componentType = unityEngineMatch;
                return true;
            }

            if (matches.Count > 1)
            {
                error = $"Ambiguous component type '{componentTypeName}'. Use a full type name. Matches: {TypeList(matches)}";
                return false;
            }

            error = $"Component type '{componentTypeName}' was not found";
            return false;
        }

        private static bool TryResolveScriptableObjectType(string typeName, out Type assetType, out string error)
        {
            assetType = null;
            error = null;

            if (string.IsNullOrWhiteSpace(typeName))
            {
                error = "typeName is required";
                return false;
            }

            var direct = Type.GetType(typeName, false);
            if (direct != null)
            {
                return ValidateScriptableObjectType(direct, out assetType, out error);
            }

            var exactFullNameMatches = new List<Type>();
            var matches = new List<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var type in GetAssemblyTypes(assembly))
                {
                    if (type == null || !typeof(ScriptableObject).IsAssignableFrom(type) || type.IsAbstract)
                    {
                        continue;
                    }

                    if (string.Equals(type.FullName, typeName, StringComparison.OrdinalIgnoreCase))
                    {
                        exactFullNameMatches.Add(type);
                    }
                    else if (string.Equals(type.Name, typeName, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(type);
                    }
                }
            }

            if (exactFullNameMatches.Count == 1)
            {
                assetType = exactFullNameMatches[0];
                return true;
            }

            if (exactFullNameMatches.Count > 1)
            {
                error = $"Ambiguous ScriptableObject type '{typeName}': {TypeList(exactFullNameMatches)}";
                return false;
            }

            if (matches.Count == 1)
            {
                assetType = matches[0];
                return true;
            }

            if (matches.Count > 1)
            {
                error = $"Ambiguous ScriptableObject type '{typeName}'. Use a full type name. Matches: {TypeList(matches)}";
                return false;
            }

            error = $"ScriptableObject type '{typeName}' was not found";
            return false;
        }

        private static bool ValidateComponentType(Type type, out Type componentType, out string error)
        {
            componentType = null;
            error = null;

            if (!typeof(Component).IsAssignableFrom(type))
            {
                error = $"{type.FullName} is not a UnityEngine.Component";
                return false;
            }

            if (type.IsAbstract)
            {
                error = $"{type.FullName} is abstract and cannot be added";
                return false;
            }

            componentType = type;
            return true;
        }

        private static bool ValidateScriptableObjectType(Type type, out Type assetType, out string error)
        {
            assetType = null;
            error = null;

            if (!typeof(ScriptableObject).IsAssignableFrom(type))
            {
                error = $"{type.FullName} is not a UnityEngine.ScriptableObject";
                return false;
            }

            if (type.IsAbstract)
            {
                error = $"{type.FullName} is abstract and cannot be created";
                return false;
            }

            assetType = type;
            return true;
        }

        private static IEnumerable<Type> GetAssemblyTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var types = new List<Type>();
                foreach (var type in ex.Types)
                {
                    if (type != null)
                    {
                        types.Add(type);
                    }
                }
                return types;
            }
            catch
            {
                return new Type[0];
            }
        }

        private static string TypeList(List<Type> types)
        {
            var names = new List<string>();
            var count = Mathf.Min(types.Count, 8);
            for (var i = 0; i < count; i++)
            {
                names.Add(types[i].FullName);
            }

            if (types.Count > count)
            {
                names.Add($"and {types.Count - count} more");
            }

            return string.Join(", ", names.ToArray());
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

        private static float Float(Dictionary<string, string> query, string key, float fallback)
        {
            return query.TryGetValue(key, out var value) && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
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

        private static string[] SplitCsv(string value)
        {
            var parts = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                parts[i] = parts[i].Trim();
            }

            return parts;
        }

        private static bool TryResolveComponentProperty(
            Dictionary<string, string> query,
            out Component component,
            out SerializedProperty property,
            out string error)
        {
            component = null;
            property = null;
            error = null;

            var go = ResolveGameObject(query);
            if (go == null)
            {
                error = "GameObject not found";
                return false;
            }

            var componentTypeName = Get(query, "componentType", string.Empty);
            if (!TryResolveComponentType(componentTypeName, out var componentType, out error))
            {
                return false;
            }

            var componentIndex = Int(query, "componentIndex", 0);
            var matches = new List<Component>();
            foreach (var candidate in go.GetComponents<Component>())
            {
                if (candidate != null && componentType.IsAssignableFrom(candidate.GetType()))
                {
                    matches.Add(candidate);
                }
            }

            if (matches.Count == 0)
            {
                error = $"GameObject '{go.name}' does not have component {componentType.FullName}";
                return false;
            }

            if (componentIndex < 0 || componentIndex >= matches.Count)
            {
                error = $"componentIndex {componentIndex} is out of range for {matches.Count} matching components";
                return false;
            }

            var propertyPath = Get(query, "propertyPath", string.Empty);
            if (string.IsNullOrWhiteSpace(propertyPath))
            {
                error = "propertyPath is required";
                return false;
            }

            component = matches[componentIndex];
            var serialized = new SerializedObject(component);
            property = serialized.FindProperty(propertyPath);
            if (property == null)
            {
                error = $"Property '{propertyPath}' was not found on component {component.GetType().FullName}";
                return false;
            }

            return true;
        }

        private static bool TryResolveLiveComponentMember(
            Dictionary<string, string> query,
            out Component component,
            out string memberPath,
            out object value,
            out string error)
        {
            component = null;
            memberPath = null;
            value = null;
            error = null;

            var go = ResolveGameObject(query);
            if (go == null)
            {
                error = "GameObject not found";
                return false;
            }

            var componentTypeName = Get(query, "componentType", string.Empty);
            if (!TryResolveComponentType(componentTypeName, out var componentType, out error))
            {
                return false;
            }

            var componentIndex = Int(query, "componentIndex", 0);
            var matches = new List<Component>();
            foreach (var candidate in go.GetComponents<Component>())
            {
                if (candidate != null && componentType.IsAssignableFrom(candidate.GetType()))
                {
                    matches.Add(candidate);
                }
            }

            if (matches.Count == 0)
            {
                error = $"GameObject '{go.name}' does not have component {componentType.FullName}";
                return false;
            }

            if (componentIndex < 0 || componentIndex >= matches.Count)
            {
                error = $"componentIndex {componentIndex} is out of range for {matches.Count} matching components";
                return false;
            }

            memberPath = Get(query, "memberPath", string.Empty);
            if (string.IsNullOrWhiteSpace(memberPath))
            {
                error = "memberPath is required";
                return false;
            }

            component = matches[componentIndex];
            if (!TryReadMemberPath(component, memberPath, out value, out error))
            {
                return false;
            }

            return true;
        }

        private static bool TryReadMemberPath(object instance, string memberPath, out object value, out string error)
        {
            value = null;
            error = null;
            if (instance == null)
            {
                error = "Runtime instance is null";
                return false;
            }

            var current = instance;
            foreach (var segment in memberPath.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (current == null)
                {
                    error = $"Member path '{memberPath}' reached a null value before '{segment}'";
                    return false;
                }

                if (!TryGetRuntimeMemberValue(current, segment, out current, out error))
                {
                    return false;
                }
            }

            value = current;
            return true;
        }

        private static bool TryGetRuntimeMemberValue(object instance, string memberName, out object value, out string error)
        {
            value = null;
            error = null;
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = instance.GetType();

            var field = type.GetField(memberName, flags);
            if (field != null)
            {
                value = field.GetValue(instance);
                return true;
            }

            var property = type.GetProperty(memberName, flags);
            if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
            {
                try
                {
                    value = property.GetValue(instance, null);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            error = $"Runtime member '{memberName}' was not found on {type.FullName}";
            return false;
        }

        private static bool TryGetJsonValue(
            Dictionary<string, string> query,
            string jsonKey,
            string rawKey,
            out string rawValue,
            out object parsedValue,
            out string error)
        {
            parsedValue = null;
            error = null;
            rawValue = string.Empty;

            if (query.TryGetValue(jsonKey, out rawValue) && !string.IsNullOrWhiteSpace(rawValue))
            {
                try
                {
                    parsedValue = MiniJson.Parse(rawValue);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            if (query.TryGetValue(rawKey, out rawValue))
            {
                parsedValue = rawValue;
                return true;
            }

            error = $"{jsonKey} or {rawKey} is required";
            return false;
        }

        private static bool TryAssignSerializedProperty(SerializedProperty property, object value, out string error)
        {
            error = null;

            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                    property.intValue = Convert.ToInt32(ToDouble(value), CultureInfo.InvariantCulture);
                    return true;
                case SerializedPropertyType.Boolean:
                    property.boolValue = ToBool(value);
                    return true;
                case SerializedPropertyType.Float:
                    property.floatValue = (float)ToDouble(value);
                    return true;
                case SerializedPropertyType.String:
                    property.stringValue = value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture);
                    return true;
                case SerializedPropertyType.LayerMask:
                    property.intValue = Convert.ToInt32(ToDouble(value), CultureInfo.InvariantCulture);
                    return true;
                case SerializedPropertyType.Enum:
                    if (value is string text)
                    {
                        for (var i = 0; i < property.enumDisplayNames.Length; i++)
                        {
                            if (string.Equals(property.enumDisplayNames[i], text, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(property.enumNames[i], text, StringComparison.OrdinalIgnoreCase))
                            {
                                property.enumValueIndex = i;
                                return true;
                            }
                        }
                    }

                    property.enumValueIndex = Convert.ToInt32(ToDouble(value), CultureInfo.InvariantCulture);
                    return true;
                case SerializedPropertyType.Color:
                    property.colorValue = ToColor(value);
                    return true;
                case SerializedPropertyType.Vector2:
                    property.vector2Value = ToVector2(value);
                    return true;
                case SerializedPropertyType.Vector3:
                    property.vector3Value = ToVector3(value);
                    return true;
                case SerializedPropertyType.Vector4:
                    property.vector4Value = ToVector4(value);
                    return true;
                case SerializedPropertyType.Quaternion:
                    property.quaternionValue = ToQuaternion(value);
                    return true;
                default:
                    error = $"SerializedPropertyType.{property.propertyType} is not supported for writes yet";
                    return false;
            }
        }

        private static double ToDouble(object value)
        {
            switch (value)
            {
                case double number:
                    return number;
                case long integer:
                    return integer;
                case int intValue:
                    return intValue;
                case float floatValue:
                    return floatValue;
                case decimal decimalValue:
                    return (double)decimalValue;
                default:
                    return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
        }

        private static bool ToBool(object value)
        {
            switch (value)
            {
                case bool boolean:
                    return boolean;
                case string text:
                    return text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("yes", StringComparison.OrdinalIgnoreCase);
                default:
                    return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }
        }

        private static IList ToListValue(object value, int requiredCount, string typeName)
        {
            var list = value as IList;
            if (list == null || list.Count < requiredCount)
            {
                throw new InvalidOperationException($"{typeName} expects an array with {requiredCount} numeric values");
            }

            return list;
        }

        private static Vector2 ToVector2(object value)
        {
            var list = ToListValue(value, 2, "Vector2");
            return new Vector2((float)ToDouble(list[0]), (float)ToDouble(list[1]));
        }

        private static Vector3 ToVector3(object value)
        {
            var list = ToListValue(value, 3, "Vector3");
            return new Vector3((float)ToDouble(list[0]), (float)ToDouble(list[1]), (float)ToDouble(list[2]));
        }

        private static Vector4 ToVector4(object value)
        {
            var list = ToListValue(value, 4, "Vector4");
            return new Vector4((float)ToDouble(list[0]), (float)ToDouble(list[1]), (float)ToDouble(list[2]), (float)ToDouble(list[3]));
        }

        private static Quaternion ToQuaternion(object value)
        {
            var list = ToListValue(value, 4, "Quaternion");
            return new Quaternion((float)ToDouble(list[0]), (float)ToDouble(list[1]), (float)ToDouble(list[2]), (float)ToDouble(list[3]));
        }

        private static Color ToColor(object value)
        {
            var v = ToVector4(value);
            return new Color(v.x, v.y, v.z, v.w);
        }

        private static EditorWindow GetGameViewWindow()
        {
            var type = Type.GetType("UnityEditor.GameView,UnityEditor");
            return type == null ? null : EditorWindow.GetWindow(type);
        }

        private static EditorWindow ResolveOrOpenEditorWindow(string target)
        {
            var window = ResolveEditorWindowByToken(target);
            if (window != null)
            {
                return window;
            }

            var menuPath = WindowMenuPath(target);
            if (!string.IsNullOrWhiteSpace(menuPath))
            {
                EditorApplication.ExecuteMenuItem(menuPath);
                return ResolveEditorWindowByToken(target);
            }

            return null;
        }

        private static EditorWindow ResolveEditorWindowByToken(string target)
        {
            var normalized = (target ?? string.Empty).Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "scene":
                case "scene_view":
                    return SceneView.lastActiveSceneView ?? EditorWindow.GetWindow<SceneView>();
                case "game":
                case "game_view":
                    return GetGameViewWindow();
                case "inspector":
                    return FindEditorWindow("UnityEditor.InspectorWindow,UnityEditor");
                case "project":
                case "project_browser":
                    return FindEditorWindow("UnityEditor.ProjectBrowser,UnityEditor");
                case "console":
                    return FindEditorWindow("UnityEditor.ConsoleWindow,UnityEditor");
                case "hierarchy":
                case "scene_hierarchy":
                    return FindEditorWindow("UnityEditor.SceneHierarchyWindow,UnityEditor");
                default:
                    return null;
            }
        }

        private static string WindowMenuPath(string target)
        {
            switch ((target ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "scene":
                case "scene_view":
                    return "Window/General/Scene";
                case "game":
                case "game_view":
                    return "Window/General/Game";
                case "inspector":
                    return "Window/General/Inspector";
                case "project":
                case "project_browser":
                    return "Window/General/Project";
                case "console":
                    return "Window/General/Console";
                case "hierarchy":
                case "scene_hierarchy":
                    return "Window/General/Hierarchy";
                default:
                    return null;
            }
        }

        private static EditorWindow FindEditorWindow(string typeName)
        {
            var type = Type.GetType(typeName, false);
            return type == null ? null : EditorWindow.GetWindow(type);
        }

        private static void FocusWindow(EditorWindow window)
        {
            window.Show();
            window.Focus();
            window.Repaint();
        }

        private static bool TryPrepareGameViewForInput(out EditorWindow gameView, out string error)
        {
            gameView = ResolveOrOpenEditorWindow("game");
            error = null;
            if (gameView == null)
            {
                error = "Unity Game view is not available";
                return false;
            }

            FocusWindow(gameView);
            InternalEditorUtilityRepaintAllViews();
            EditorApplication.QueuePlayerLoopUpdate();
            FocusWindow(gameView);
            return true;
        }

        private static Dictionary<string, object> EditorWindowPayload(EditorWindow window, string target)
        {
            return new Dictionary<string, object>
            {
                { "target", target },
                { "title", window.titleContent == null ? string.Empty : window.titleContent.text },
                { "windowType", window.GetType().FullName },
                { "position", new Dictionary<string, object>
                    {
                        { "x", window.position.x },
                        { "y", window.position.y },
                        { "width", window.position.width },
                        { "height", window.position.height }
                    }
                }
            };
        }

        private static void InternalEditorUtilityRepaintAllViews()
        {
            var type = Type.GetType("UnityEditorInternal.InternalEditorUtility,UnityEditor");
            var method = type == null ? null : type.GetMethod("RepaintAllViews", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            method?.Invoke(null, null);
        }

        private static Event BuildKeyEvent(EventType eventType, KeyCode keyCode, string character)
        {
            var evt = new Event
            {
                type = eventType,
                keyCode = keyCode
            };

            if (!string.IsNullOrEmpty(character))
            {
                evt.character = character[0];
            }

            return evt;
        }

        private static Event BuildMouseEvent(EventType eventType, Vector2 guiPoint, int button)
        {
            return new Event
            {
                type = eventType,
                button = button,
                mousePosition = guiPoint,
                clickCount = eventType == EventType.MouseDown || eventType == EventType.MouseUp ? 1 : 0
            };
        }

        private static void SendGameViewEvent(EditorWindow gameView, Event evt)
        {
            gameView.SendEvent(evt);
            gameView.Repaint();
        }

        private static bool TryInjectKeyboardIntoInputSystem(KeyCode keyCode, string eventType, out string inputSystemKey, out string error)
        {
            inputSystemKey = null;
#if ENABLE_INPUT_SYSTEM
            error = null;

            if (!EditorApplication.isPlaying)
            {
                error = "Input System keyboard injection requires Play Mode";
                return false;
            }

            if (InputKeyboard.current == null)
            {
                error = "Input System keyboard device is not available";
                return false;
            }

            if (!TryMapKeyCodeToInputKey(keyCode, out var mappedKey))
            {
                error = $"KeyCode '{keyCode}' is not mapped to Unity Input System Key";
                return false;
            }

            switch (eventType)
            {
                case "down":
                    PressedInputKeys.Add(mappedKey);
                    QueueKeyboardState();
                    break;
                case "up":
                    PressedInputKeys.Remove(mappedKey);
                    QueueKeyboardState();
                    break;
                default:
                    PressedInputKeys.Add(mappedKey);
                    QueueKeyboardState();
                    PressedInputKeys.Remove(mappedKey);
                    QueueKeyboardState();
                    break;
            }

            inputSystemKey = mappedKey.ToString();
            return true;
#else
            error = "Unity Input System support is not enabled in this project";
            return false;
#endif
        }

        private static bool TryInjectMouseIntoInputSystem(Vector2 screenPoint, int button, string eventType, out string error)
        {
#if ENABLE_INPUT_SYSTEM
            error = null;

            if (!EditorApplication.isPlaying)
            {
                error = "Input System mouse injection requires Play Mode";
                return false;
            }

            if (InputMouse.current == null)
            {
                error = "Input System mouse device is not available";
                return false;
            }

            switch (eventType)
            {
                case "move":
                    QueueMouseState(screenPoint, null, null, 0);
                    break;
                case "down":
                    QueueMouseState(screenPoint, button, true, 1);
                    break;
                case "up":
                    QueueMouseState(screenPoint, button, false, 1);
                    break;
                default:
                    QueueMouseState(screenPoint, null, null, 0);
                    QueueMouseState(screenPoint, button, true, 1);
                    QueueMouseState(screenPoint, button, false, 1);
                    break;
            }

            return true;
#else
            error = "Unity Input System support is not enabled in this project";
            return false;
#endif
        }

#if ENABLE_INPUT_SYSTEM
        private static void QueueKeyboardState()
        {
            var state = new InputKeyboardState();
            foreach (var pressedKey in PressedInputKeys)
            {
                state.Press(pressedKey);
            }

            InputSystemApi.QueueStateEvent(InputKeyboard.current, state);
            EditorApplication.QueuePlayerLoopUpdate();
        }

        private static void QueueMouseState(Vector2 screenPoint, int? button, bool? pressed, int clickCount)
        {
            if (button.HasValue && pressed.HasValue)
            {
                if (pressed.Value)
                {
                    PressedMouseButtons.Add(button.Value);
                }
                else
                {
                    PressedMouseButtons.Remove(button.Value);
                }
            }

            var delta = screenPoint - _lastInjectedMousePosition;
            _lastInjectedMousePosition = screenPoint;

            var state = new InputMouseState
            {
                position = screenPoint,
                delta = delta,
                clickCount = (ushort)Mathf.Clamp(clickCount, 0, ushort.MaxValue)
            };

            foreach (var pressedButton in PressedMouseButtons)
            {
                if (TryMapMouseButton(pressedButton, out var mappedButton))
                {
                    state = state.WithButton(mappedButton, true);
                }
            }

            InputSystemApi.QueueStateEvent(InputMouse.current, state);
            EditorApplication.QueuePlayerLoopUpdate();
        }

        private static bool TryMapMouseButton(int button, out InputMouseButton mappedButton)
        {
            switch (button)
            {
                case 0:
                    mappedButton = InputMouseButton.Left;
                    return true;
                case 1:
                    mappedButton = InputMouseButton.Right;
                    return true;
                case 2:
                    mappedButton = InputMouseButton.Middle;
                    return true;
                case 3:
                    mappedButton = InputMouseButton.Forward;
                    return true;
                case 4:
                    mappedButton = InputMouseButton.Back;
                    return true;
                default:
                    mappedButton = InputMouseButton.Left;
                    return false;
            }
        }

        private static bool TryMapKeyCodeToInputKey(KeyCode keyCode, out InputKey mappedKey)
        {
            if (Enum.TryParse(keyCode.ToString(), true, out mappedKey))
            {
                return true;
            }

            switch (keyCode)
            {
                case KeyCode.Alpha0:
                    mappedKey = InputKey.Digit0;
                    return true;
                case KeyCode.Alpha1:
                    mappedKey = InputKey.Digit1;
                    return true;
                case KeyCode.Alpha2:
                    mappedKey = InputKey.Digit2;
                    return true;
                case KeyCode.Alpha3:
                    mappedKey = InputKey.Digit3;
                    return true;
                case KeyCode.Alpha4:
                    mappedKey = InputKey.Digit4;
                    return true;
                case KeyCode.Alpha5:
                    mappedKey = InputKey.Digit5;
                    return true;
                case KeyCode.Alpha6:
                    mappedKey = InputKey.Digit6;
                    return true;
                case KeyCode.Alpha7:
                    mappedKey = InputKey.Digit7;
                    return true;
                case KeyCode.Alpha8:
                    mappedKey = InputKey.Digit8;
                    return true;
                case KeyCode.Alpha9:
                    mappedKey = InputKey.Digit9;
                    return true;
                case KeyCode.Return:
                    mappedKey = InputKey.Enter;
                    return true;
                case KeyCode.KeypadEnter:
                    mappedKey = InputKey.NumpadEnter;
                    return true;
                case KeyCode.LeftControl:
                    mappedKey = InputKey.LeftCtrl;
                    return true;
                case KeyCode.RightControl:
                    mappedKey = InputKey.RightCtrl;
                    return true;
                case KeyCode.LeftAlt:
                    mappedKey = InputKey.LeftAlt;
                    return true;
                case KeyCode.RightAlt:
                    mappedKey = InputKey.RightAlt;
                    return true;
                case KeyCode.LeftShift:
                    mappedKey = InputKey.LeftShift;
                    return true;
                case KeyCode.RightShift:
                    mappedKey = InputKey.RightShift;
                    return true;
                case KeyCode.BackQuote:
                    mappedKey = InputKey.Backquote;
                    return true;
                case KeyCode.Escape:
                    mappedKey = InputKey.Escape;
                    return true;
                case KeyCode.Space:
                    mappedKey = InputKey.Space;
                    return true;
                case KeyCode.UpArrow:
                    mappedKey = InputKey.UpArrow;
                    return true;
                case KeyCode.DownArrow:
                    mappedKey = InputKey.DownArrow;
                    return true;
                case KeyCode.LeftArrow:
                    mappedKey = InputKey.LeftArrow;
                    return true;
                case KeyCode.RightArrow:
                    mappedKey = InputKey.RightArrow;
                    return true;
                case KeyCode.Backspace:
                    mappedKey = InputKey.Backspace;
                    return true;
                case KeyCode.Delete:
                    mappedKey = InputKey.Delete;
                    return true;
                case KeyCode.Tab:
                    mappedKey = InputKey.Tab;
                    return true;
                default:
                    mappedKey = default;
                    return false;
            }
        }
#endif

        private static Vector2 ScreenToGameViewPoint(EditorWindow gameView, Vector2 screenPoint)
        {
            return new Vector2(screenPoint.x, gameView.position.height - screenPoint.y);
        }

        private static Vector2 RectTransformScreenPoint(RectTransform rectTransform)
        {
            var canvas = rectTransform.GetComponentInParent<Canvas>();
            var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? (canvas.worldCamera != null ? canvas.worldCamera : Camera.main)
                : null;
            var worldCenter = rectTransform.TransformPoint(rectTransform.rect.center);
            return RectTransformUtility.WorldToScreenPoint(camera, worldCenter);
        }

        private static void CaptureRuntimeLog(string condition, string stackTrace, LogType type)
        {
            lock (RecentLogsLock)
            {
                RecentLogs.Add(new RuntimeLogEntry
                {
                    TimestampUtc = DateTime.UtcNow,
                    Type = type.ToString(),
                    Message = condition ?? string.Empty,
                    StackTrace = stackTrace ?? string.Empty
                });

                if (RecentLogs.Count > MaxRecentLogs)
                {
                    RecentLogs.RemoveRange(0, RecentLogs.Count - MaxRecentLogs);
                }
            }
        }

        private static Dictionary<string, object> RuntimeLogPayload(RuntimeLogEntry entry)
        {
            return new Dictionary<string, object>
            {
                { "timeUtc", entry.TimestampUtc.ToString("o", CultureInfo.InvariantCulture) },
                { "type", entry.Type },
                { "message", entry.Message },
                { "stackTrace", entry.StackTrace }
            };
        }

        private static bool MatchesComparison(object actual, object expected, string comparison)
        {
            switch (comparison)
            {
                case "not_equals":
                case "not-equals":
                    return MiniJson.Serialize(actual) != MiniJson.Serialize(expected);
                case "contains":
                    return Convert.ToString(actual, CultureInfo.InvariantCulture)
                        .IndexOf(Convert.ToString(expected, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase) >= 0;
                case "greater":
                    return ToDouble(actual) > ToDouble(expected);
                case "less":
                    return ToDouble(actual) < ToDouble(expected);
                default:
                    return MiniJson.Serialize(actual) == MiniJson.Serialize(expected);
            }
        }

        private static Dictionary<string, object> TestRunPayload(TestRunState run)
        {
            return new Dictionary<string, object>
            {
                { "runId", run.Id },
                { "mode", run.Mode },
                { "status", run.Status },
                { "startedUtc", run.StartedUtc.ToString("o", CultureInfo.InvariantCulture) },
                { "finishedUtc", run.FinishedUtc.HasValue ? run.FinishedUtc.Value.ToString("o", CultureInfo.InvariantCulture) : null },
                { "totalCount", run.TotalCount },
                { "passedCount", run.PassedCount },
                { "failedCount", run.FailedCount },
                { "skippedCount", run.SkippedCount },
                { "currentTest", run.CurrentTest },
                { "results", run.Results }
            };
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

        private sealed class SceneSnapshot
        {
            public string Id { get; set; }
            public DateTime CapturedUtc { get; set; }
            public string SceneName { get; set; }
            public string ScenePath { get; set; }
            public bool IsDirty { get; set; }
            public Dictionary<string, SnapshotObject> Objects { get; set; }
        }

        private sealed class SnapshotObject
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Path { get; set; }
            public bool ActiveSelf { get; set; }
            public bool ActiveInHierarchy { get; set; }
            public string Tag { get; set; }
            public string Layer { get; set; }
            public Dictionary<string, object> TransformSignature { get; set; }
            public List<object> ComponentsSignature { get; set; }
        }

        private sealed class ConsoleCheckpoint
        {
            public string Id { get; set; }
            public int EntryIndex { get; set; }
            public DateTime CreatedUtc { get; set; }
        }

        private sealed class EditorSessionState
        {
            public string Id { get; set; }
            public DateTime CapturedUtc { get; set; }
            public string FocusedWindowTarget { get; set; }
            public string SelectedObjectPath { get; set; }
            public string SelectedAssetPath { get; set; }
        }

        private sealed class RuntimeLogEntry
        {
            public DateTime TimestampUtc { get; set; }
            public string Type { get; set; }
            public string Message { get; set; }
            public string StackTrace { get; set; }
        }

        private enum ExistingBridgeStatus
        {
            Unknown,
            Reachable,
            PortBusy
        }

        private sealed class TestRunState
        {
            public string Id { get; set; }
            public string Mode { get; set; }
            public string Status { get; set; }
            public DateTime StartedUtc { get; set; }
            public DateTime? FinishedUtc { get; set; }
            public int TotalCount { get; set; }
            public int PassedCount { get; set; }
            public int FailedCount { get; set; }
            public int SkippedCount { get; set; }
            public string CurrentTest { get; set; }
            public bool IsComplete { get; set; }
            public TestRunnerApi Api { get; set; }
            public CodexTestCallbacks Callbacks { get; set; }
            public List<object> Results { get; } = new List<object>();
        }

        private sealed class CodexTestCallbacks : ICallbacks
        {
            private readonly TestRunState _run;

            public CodexTestCallbacks(TestRunState run)
            {
                _run = run;
            }

            public void RunStarted(ITestAdaptor testsToRun)
            {
                _run.Status = "running";
                _run.TotalCount = CountTests(testsToRun);
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                _run.Status = _run.FailedCount > 0 ? "failed" : "passed";
                _run.FinishedUtc = DateTime.UtcNow;
                _run.IsComplete = true;
            }

            public void TestStarted(ITestAdaptor test)
            {
                if (test == null || test.IsSuite)
                {
                    return;
                }

                _run.CurrentTest = test.FullName;
            }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result == null || result.Test == null || result.Test.IsSuite)
                {
                    return;
                }

                var status = result.TestStatus.ToString();
                switch (status.ToLowerInvariant())
                {
                    case "passed":
                        _run.PassedCount++;
                        break;
                    case "failed":
                        _run.FailedCount++;
                        break;
                    default:
                        _run.SkippedCount++;
                        break;
                }

                _run.Results.Add(new Dictionary<string, object>
                {
                    { "name", result.Name },
                    { "fullName", result.FullName },
                    { "status", status },
                    { "duration", result.Duration },
                    { "message", result.Message },
                    { "stackTrace", result.StackTrace }
                });
            }

            private static int CountTests(ITestAdaptor test)
            {
                if (test == null)
                {
                    return 0;
                }

                if (!test.IsSuite)
                {
                    return 1;
                }

                var count = 0;
                foreach (var child in test.Children)
                {
                    count += CountTests(child);
                }

                return count;
            }
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

        public static object Parse(string json)
        {
            var parser = new Parser(json);
            return parser.Parse();
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

        private sealed class Parser
        {
            private readonly string _json;
            private int _index;

            public Parser(string json)
            {
                _json = json ?? string.Empty;
            }

            public object Parse()
            {
                var result = ParseValue();
                SkipWhitespace();
                if (_index != _json.Length)
                {
                    throw new FormatException("Unexpected trailing JSON content");
                }

                return result;
            }

            private object ParseValue()
            {
                SkipWhitespace();
                if (_index >= _json.Length)
                {
                    throw new FormatException("Unexpected end of JSON");
                }

                var c = _json[_index];
                if (c == '{')
                {
                    return ParseObject();
                }
                if (c == '[')
                {
                    return ParseArray();
                }
                if (c == '"')
                {
                    return ParseString();
                }
                if (c == 't' && Match("true"))
                {
                    return true;
                }
                if (c == 'f' && Match("false"))
                {
                    return false;
                }
                if (c == 'n' && Match("null"))
                {
                    return null;
                }
                return ParseNumber();
            }

            private Dictionary<string, object> ParseObject()
            {
                Expect('{');
                var result = new Dictionary<string, object>();
                SkipWhitespace();
                if (Peek('}'))
                {
                    _index++;
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    var key = ParseString();
                    SkipWhitespace();
                    Expect(':');
                    result[key] = ParseValue();
                    SkipWhitespace();
                    if (Peek('}'))
                    {
                        _index++;
                        return result;
                    }
                    Expect(',');
                }
            }

            private List<object> ParseArray()
            {
                Expect('[');
                var result = new List<object>();
                SkipWhitespace();
                if (Peek(']'))
                {
                    _index++;
                    return result;
                }

                while (true)
                {
                    result.Add(ParseValue());
                    SkipWhitespace();
                    if (Peek(']'))
                    {
                        _index++;
                        return result;
                    }
                    Expect(',');
                }
            }

            private string ParseString()
            {
                Expect('"');
                var builder = new StringBuilder();
                while (_index < _json.Length)
                {
                    var c = _json[_index++];
                    if (c == '"')
                    {
                        return builder.ToString();
                    }

                    if (c != '\\')
                    {
                        builder.Append(c);
                        continue;
                    }

                    if (_index >= _json.Length)
                    {
                        throw new FormatException("Invalid JSON escape");
                    }

                    var escaped = _json[_index++];
                    switch (escaped)
                    {
                        case '"':
                        case '\\':
                        case '/':
                            builder.Append(escaped);
                            break;
                        case 'b':
                            builder.Append('\b');
                            break;
                        case 'f':
                            builder.Append('\f');
                            break;
                        case 'n':
                            builder.Append('\n');
                            break;
                        case 'r':
                            builder.Append('\r');
                            break;
                        case 't':
                            builder.Append('\t');
                            break;
                        case 'u':
                            if (_index + 4 > _json.Length)
                            {
                                throw new FormatException("Invalid unicode escape");
                            }
                            var hex = _json.Substring(_index, 4);
                            builder.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            _index += 4;
                            break;
                        default:
                            throw new FormatException($"Invalid JSON escape '\\{escaped}'");
                    }
                }

                throw new FormatException("Unterminated JSON string");
            }

            private object ParseNumber()
            {
                var start = _index;
                if (Peek('-'))
                {
                    _index++;
                }

                while (_index < _json.Length && char.IsDigit(_json[_index]))
                {
                    _index++;
                }

                var isFloat = false;
                if (Peek('.'))
                {
                    isFloat = true;
                    _index++;
                    while (_index < _json.Length && char.IsDigit(_json[_index]))
                    {
                        _index++;
                    }
                }

                if (_index < _json.Length && (_json[_index] == 'e' || _json[_index] == 'E'))
                {
                    isFloat = true;
                    _index++;
                    if (_index < _json.Length && (_json[_index] == '-' || _json[_index] == '+'))
                    {
                        _index++;
                    }
                    while (_index < _json.Length && char.IsDigit(_json[_index]))
                    {
                        _index++;
                    }
                }

                var raw = _json.Substring(start, _index - start);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    throw new FormatException("Expected JSON value");
                }

                return isFloat
                    ? (object)double.Parse(raw, CultureInfo.InvariantCulture)
                    : long.Parse(raw, CultureInfo.InvariantCulture);
            }

            private bool Match(string text)
            {
                if (_index + text.Length > _json.Length || string.Compare(_json, _index, text, 0, text.Length, StringComparison.Ordinal) != 0)
                {
                    return false;
                }

                _index += text.Length;
                return true;
            }

            private void SkipWhitespace()
            {
                while (_index < _json.Length && char.IsWhiteSpace(_json[_index]))
                {
                    _index++;
                }
            }

            private void Expect(char expected)
            {
                SkipWhitespace();
                if (_index >= _json.Length || _json[_index] != expected)
                {
                    throw new FormatException($"Expected '{expected}'");
                }
                _index++;
            }

            private bool Peek(char expected)
            {
                return _index < _json.Length && _json[_index] == expected;
            }
        }
    }
}
