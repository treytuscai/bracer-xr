using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Experiments.Cli
{
    /// <summary>
    /// In-app command CLI for driving experiments from a laptop. Listens on a loopback TCP
    /// port; a connection sends newline-delimited commands ("load chooseColorWithColorwheel",
    /// "clear", "screenshot") and reads back a one-line reply. Reach it over adb with no IP
    /// discovery: `adb forward tcp:9999 tcp:9999`, then talk to localhost. See Tools/expctl.
    ///
    /// Why TCP over a loopback socket rather than an Android broadcast intent: a BroadcastReceiver
    /// can't be authored in C# (AndroidJavaProxy proxies interfaces, not the abstract Receiver
    /// class), so intents would force a Java/AAR plugin. A TcpListener is pure C#, gives a return
    /// channel for free, and `adb forward` makes it work tethered or over wifi.
    ///
    /// Threading: the accept/read loop runs on a background thread and only enqueues requests.
    /// Every handler runs on the Unity main thread (drained in Update), because they touch the
    /// scene graph. The connection thread blocks on each request until the main thread answers.
    ///
    /// Commands are split in two: GLOBAL verbs owned here (load/reload/scenes/screenshot/ping/
    /// quit/help) and SCENE verbs contributed by controllers via <see cref="IExperimentCommands"/>,
    /// rescanned on every scene load. The server never references an experiment's type.
    /// </summary>
    public sealed class ExperimentCommandServer : MonoBehaviour
    {
        const int Port = 9999;
        const int RequestTimeoutMs = 5000;

        delegate string Handler(IReadOnlyDictionary<string, string> args);

        // Global verbs survive scene loads; scene verbs are rebuilt by the active scene's providers.
        readonly Dictionary<string, Handler> _global = new Dictionary<string, Handler>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Func<IReadOnlyDictionary<string, string>, string>> _scene =
            new Dictionary<string, Func<IReadOnlyDictionary<string, string>, string>>(StringComparer.OrdinalIgnoreCase);

        readonly ConcurrentQueue<Request> _inbox = new ConcurrentQueue<Request>();

        TcpListener _listener;
        Thread _acceptThread;
        volatile bool _running;

        /// <summary>
        /// Spawns the server automatically once the first scene is up, with no GameObject to place
        /// in any scene. Survives scene changes via DontDestroyOnLoad.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            var go = new GameObject("[ExperimentCommandServer]");
            DontDestroyOnLoad(go);
            go.AddComponent<ExperimentCommandServer>();
        }

        void Awake()
        {
            RegisterGlobalCommands();
            RebuildSceneCommands(SceneManager.GetActiveScene());
            SceneManager.sceneLoaded += OnSceneLoaded;
            StartListener();
        }

        void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            StopListener();
        }

        // ------------------------------------------------------------------
        // MAIN-THREAD DISPATCH
        // ------------------------------------------------------------------

        void Update()
        {
            while (_inbox.TryDequeue(out Request req))
            {
                try { req.Response = Dispatch(req.Line); }
                catch (Exception e) { req.Response = "error: " + e.Message; }
                finally { req.Done.Set(); }
            }
        }

        string Dispatch(string line)
        {
            string verb = ParseCommand(line, out IReadOnlyDictionary<string, string> args);
            if (string.IsNullOrEmpty(verb)) return "error: empty command";

            if (_global.TryGetValue(verb, out Handler g)) return g(args);
            if (_scene.TryGetValue(verb, out var s)) return s(args);

            return $"error: unknown command '{verb}' (try 'help')";
        }

        // ------------------------------------------------------------------
        // COMMAND TABLES
        // ------------------------------------------------------------------

        void RegisterGlobalCommands()
        {
            _global["ping"] = _ => "pong";

            _global["help"] = _ =>
            {
                var sb = new StringBuilder("global: ");
                sb.Append(string.Join(", ", _global.Keys));
                sb.Append(" | scene: ");
                sb.Append(_scene.Count > 0 ? string.Join(", ", _scene.Keys) : "(none)");
                return sb.ToString();
            };

            _global["scenes"] = _ =>
            {
                int n = SceneManager.sceneCountInBuildSettings;
                var names = new string[n];
                for (int i = 0; i < n; i++)
                    names[i] = Path.GetFileNameWithoutExtension(SceneUtility.GetScenePathByBuildIndex(i));
                return string.Join(", ", names);
            };

            _global["load"] = args =>
            {
                if (!args.TryGetValue("0", out string scene) || string.IsNullOrWhiteSpace(scene))
                    return "error: usage 'load <sceneName>'";
                if (!Application.CanStreamedLevelBeLoaded(scene))
                    return $"error: '{scene}' not in Build Settings";
                SceneManager.LoadScene(scene, LoadSceneMode.Single);
                return "loading " + scene;
            };

            _global["reload"] = _ =>
            {
                Scene active = SceneManager.GetActiveScene();
                SceneManager.LoadScene(active.name, LoadSceneMode.Single);
                return "reloading " + active.name;
            };

            _global["screenshot"] = args =>
            {
                string name = args.TryGetValue("0", out string n) && !string.IsNullOrWhiteSpace(n)
                    ? n
                    : $"shot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                return CaptureScreenshot(name);
            };

            _global["quit"] = _ =>
            {
                Application.Quit();
                return "quitting";
            };
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode) => RebuildSceneCommands(scene);

        /// <summary>
        /// Rebuilds the scene-scoped verb table by asking every <see cref="IExperimentCommands"/>
        /// provider in the freshly loaded scene to register its commands. Called on each load so
        /// stale verbs from the previous experiment don't linger.
        /// </summary>
        void RebuildSceneCommands(Scene scene)
        {
            _scene.Clear();
            if (!scene.IsValid()) return;

            // FindObjectsOfType is fine here: scene loads are rare and operator-driven.
            var providers = FindObjectsOfType<MonoBehaviour>(includeInactive: true);
            foreach (MonoBehaviour mb in providers)
            {
                if (mb is IExperimentCommands provider)
                    provider.RegisterCommands(_scene);
            }
        }

        // ------------------------------------------------------------------
        // SCREENSHOT
        // Render the active camera to an offscreen target and write a PNG to persistentDataPath
        // (/sdcard/Android/data/<pkg>/files on Quest), so `adb pull` can fetch it without root.
        // ScreenCapture.CaptureScreenshot is avoided: in XR it captures the distorted eye buffer.
        // ------------------------------------------------------------------

        string CaptureScreenshot(string fileName)
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                foreach (Camera c in Camera.allCameras)
                {
                    if (c.isActiveAndEnabled) { cam = c; break; }
                }
            }
            if (cam == null) return "error: no active camera";

            const int w = 1280, h = 720;
            var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
            RenderTexture prevTarget = cam.targetTexture;
            RenderTexture prevActive = RenderTexture.active;

            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply(false);
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }

            string path = Path.Combine(Application.persistentDataPath, fileName);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Destroy(tex);
            return "saved " + path;
        }

        // ------------------------------------------------------------------
        // PARSING
        // "verb a b key=val" -> verb, { "0":"a", "1":"b", "key":"val" }.
        // Positional args after the verb are indexed from 0; key=value pairs keep their key.
        // ------------------------------------------------------------------

        static string ParseCommand(string line, out IReadOnlyDictionary<string, string> args)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            args = map;

            string[] tokens = line.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return null;

            int positional = 0;
            for (int i = 1; i < tokens.Length; i++)
            {
                int eq = tokens[i].IndexOf('=');
                if (eq > 0)
                    map[tokens[i].Substring(0, eq)] = tokens[i].Substring(eq + 1);
                else
                    map[(positional++).ToString()] = tokens[i];
            }
            return tokens[0];
        }

        // ------------------------------------------------------------------
        // TCP LISTENER (background thread)
        // ------------------------------------------------------------------

        void StartListener()
        {
            _running = true;
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "ExpCmdServer" };
            _acceptThread.Start();
            Debug.Log($"[ExperimentCommandServer] listening on 127.0.0.1:{Port} " +
                      $"(adb forward tcp:{Port} tcp:{Port})");
        }

        void StopListener()
        {
            _running = false;
            try { _listener?.Stop(); } catch { /* shutting down */ }

            // Release any connection thread blocked waiting on the main thread.
            while (_inbox.TryDequeue(out Request req)) req.Done.Set();
            _acceptThread?.Join(200);
        }

        void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch { break; } // listener stopped

                using (client)
                using (NetworkStream stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
                {
                    string line;
                    while (_running && (line = reader.ReadLine()) != null)
                    {
                        if (line.Length == 0) continue;

                        var req = new Request(line);
                        _inbox.Enqueue(req);

                        // Block until Update() answers (or the app is tearing down / hung).
                        string reply = req.Done.Wait(RequestTimeoutMs) ? req.Response : "error: timeout";
                        try { writer.WriteLine(reply); }
                        catch { break; } // client gone
                    }
                }
            }
        }

        /// <summary>One queued command awaiting a main-thread answer.</summary>
        sealed class Request
        {
            public readonly string Line;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public string Response = "error: no response";
            public Request(string line) { Line = line; }
        }
    }
}
