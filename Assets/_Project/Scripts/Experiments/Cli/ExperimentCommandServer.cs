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
    /// In-app HTTP command server for driving experiments from a laptop.
    /// Listens on loopback HTTP; send commands with curl via adb forward:
    ///   adb forward tcp:9999 tcp:9999
    ///   curl -s http://localhost:9999/ -d "ping"
    /// See Tools/expctl for the wrapper script.
    ///
    /// Transport: HttpListener (plain HTTP POST, command in body, response as plain text).
    /// curl is reliable where nc is not — it handles the full request/response cycle correctly
    /// regardless of macOS version.
    ///
    /// Threading: HttpListener.GetContext() blocks on a background thread. Each request is
    /// dispatched synchronously: the bg thread enqueues it, blocks until Unity's Update()
    /// answers, then writes the HTTP response. Handlers run on the main thread.
    ///
    /// Commands: GLOBAL verbs (load/reload/scenes/screenshot/ping/quit/help) live here.
    /// SCENE verbs are contributed by controllers via <see cref="IExperimentCommands"/>,
    /// rescanned on every scene load — the server never references an experiment type.
    /// </summary>
    public sealed class ExperimentCommandServer : MonoBehaviour
    {
        const int Port = 9999;
        const int RequestTimeoutMs = 5000;

        delegate string Handler(IReadOnlyDictionary<string, string> args);

        readonly Dictionary<string, Handler> _global =
            new Dictionary<string, Handler>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Func<IReadOnlyDictionary<string, string>, string>> _scene =
            new Dictionary<string, Func<IReadOnlyDictionary<string, string>, string>>(StringComparer.OrdinalIgnoreCase);

        readonly ConcurrentQueue<Request> _inbox = new ConcurrentQueue<Request>();

        TcpListener _listener;
        Thread _acceptThread;
        volatile bool _running;

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
                    names[i] = Path.GetFileNameWithoutExtension(
                        SceneUtility.GetScenePathByBuildIndex(i));
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

            _global["quit"] = _ => { Application.Quit(); return "quitting"; };
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode) => RebuildSceneCommands(scene);

        void RebuildSceneCommands(Scene scene)
        {
            _scene.Clear();
            if (!scene.IsValid()) return;

            var providers = FindObjectsOfType<MonoBehaviour>(includeInactive: true);
            foreach (MonoBehaviour mb in providers)
            {
                if (mb is IExperimentCommands p)
                    p.RegisterCommands(_scene);
            }
        }

        // ------------------------------------------------------------------
        // SCREENSHOT
        // ------------------------------------------------------------------

        string CaptureScreenshot(string fileName)
        {
            Camera cam = Camera.main;
            if (cam == null)
                foreach (Camera c in Camera.allCameras)
                    if (c.isActiveAndEnabled) { cam = c; break; }
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
        // PARSING — "verb a b key=val" -> verb + positional/named args dict
        // ------------------------------------------------------------------

        static string ParseCommand(string line, out IReadOnlyDictionary<string, string> args)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            args = map;

            string[] tokens = line.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return null;

            int pos = 0;
            for (int i = 1; i < tokens.Length; i++)
            {
                int eq = tokens[i].IndexOf('=');
                if (eq > 0) map[tokens[i].Substring(0, eq)] = tokens[i].Substring(eq + 1);
                else        map[(pos++).ToString()] = tokens[i];
            }
            return tokens[0];
        }

        // ------------------------------------------------------------------
        // HTTP LISTENER (background thread)
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
            try { _listener?.Stop(); } catch { }
            while (_inbox.TryDequeue(out Request req)) req.Done.Set();
            _acceptThread?.Join(200);
        }

        void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch { break; }

                try { HandleClient(client); }
                catch { }
                finally { client.Close(); }
            }
        }

        // Minimal HTTP/1.1 server: reads headers to find Content-Length, reads the body
        // as the command, dispatches on the main thread, writes a plain-text 200 response.
        // Bypasses Mono's HttpListener entirely — no host-matching quirks.
        void HandleClient(TcpClient client)
        {
            NetworkStream stream = client.GetStream();

            // Read request line + headers into a byte buffer until \r\n\r\n.
            var headerBuf = new List<byte>(512);
            int b;
            while ((b = stream.ReadByte()) != -1)
            {
                headerBuf.Add((byte)b);
                if (headerBuf.Count >= 4)
                {
                    int n = headerBuf.Count;
                    if (headerBuf[n-4] == '\r' && headerBuf[n-3] == '\n' &&
                        headerBuf[n-2] == '\r' && headerBuf[n-1] == '\n')
                        break;
                }
            }

            string headers = Encoding.UTF8.GetString(headerBuf.ToArray());

            // Extract Content-Length from headers.
            int contentLength = 0;
            foreach (string headerLine in headers.Split('\n'))
            {
                if (headerLine.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(headerLine.Substring(15).Trim(), out contentLength);
                    break;
                }
            }

            // Read body.
            string body = "";
            if (contentLength > 0)
            {
                byte[] bodyBytes = new byte[contentLength];
                int total = 0;
                while (total < contentLength)
                {
                    int read = stream.Read(bodyBytes, total, contentLength - total);
                    if (read == 0) break;
                    total += read;
                }
                body = Encoding.UTF8.GetString(bodyBytes, 0, total).Trim();
            }

            string reply;
            if (string.IsNullOrWhiteSpace(body))
            {
                reply = "error: empty command";
            }
            else
            {
                var req = new Request(body);
                _inbox.Enqueue(req);
                reply = req.Done.Wait(RequestTimeoutMs) ? req.Response : "error: timeout";
            }

            byte[] replyBytes = Encoding.UTF8.GetBytes(reply + "\n");
            string httpResponse =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                $"Content-Length: {replyBytes.Length}\r\n" +
                "Connection: close\r\n" +
                "\r\n";
            byte[] responseHeader = Encoding.UTF8.GetBytes(httpResponse);
            stream.Write(responseHeader, 0, responseHeader.Length);
            stream.Write(replyBytes, 0, replyBytes.Length);
            stream.Flush();
        }

        sealed class Request
        {
            public readonly string Line;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public string Response = "error: no response";
            public Request(string line) { Line = line; }
        }
    }
}
