using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Microsoft.Playfab.Gaming.GSDK.CSharp;
using Newtonsoft.Json;
using PlayFab;

namespace WindowsRunnerCSharp
{
    ///-------------------------------------------------------------------------------
    ///   Simple executable that integrates with PlayFab's Gameserver SDK (GSDK).
    ///   It starts an http server that will respond to GET requests with a json file
    ///   containing whatever configuration values it read from the GSDK.
    ///-------------------------------------------------------------------------------
    class Program
    {
        private static HttpListener _listener = new HttpListener();
        const string ListeningPortKey = "game";

        const string AssetFilePath = @"C:\Assets\testassetfile.txt";
        private const string GameCertAlias = "winRunnerTestCert";

        private static List<ConnectedPlayer> players = new List<ConnectedPlayer>();
        private static List<string> matchPlayers = new List<string>();
        private static int requestCount = 0;

        private static bool _isActivated = false;
        private static string _assetFileText = String.Empty;
        private static string _installedCertThumbprint = String.Empty;
        private static DateTimeOffset _nextMaintenance = DateTimeOffset.MinValue;

        static void OnShutdown()
        {
            LogMessage("Shutting down...");
            _listener.Stop();
            _listener.Close();
        }

        static bool IsHealthy()
        {
            // Should return whether this game server is healthy
            return true;
        }

        static void OnMaintenanceScheduled(DateTimeOffset time)
        {
            LogMessage($"Maintenance Scheduled at: {time}");
            _nextMaintenance = time;
        }
        
        static void StartVSMon()
        {
            string batDir = Directory.GetCurrentDirectory();
            var proc = new Process();
            proc.StartInfo.WorkingDirectory = batDir;
            proc.StartInfo.FileName = "Powershell.exe";
            proc.StartInfo.Arguments = "  -File .\\startvsmon.ps1";
            proc.StartInfo.CreateNoWindow = true;
            proc.Start();
            proc.WaitForExit();
        }

        static async System.Threading.Tasks.Task Main(string[] args)
        {
            PlayFabSettings.staticSettings.TitleId = "AB20";
            PlayFabSettings.staticSettings.DeveloperSecretKey = "SWWSEDPI4I5SHW3RGJ6ATTXS6PBSZ5TTKE3RI1TEBQPWX9CPJU";
            StartVSMon();
            // GSDK Setup
            try
            {
                GameserverSDK.Start();
            }
            catch (Microsoft.Playfab.Gaming.GSDK.CSharp.GSDKInitializationException initEx)
            {
                LogMessage("Cannot start GSDK. Please make sure the MockAgent is running. ", false);
                LogMessage($"Got Exception: {initEx.ToString()}", false);
                return;
            }
            catch (Exception ex)
            {
                LogMessage($"Got Exception: {ex.ToString()}", false);
            }

            GameserverSDK.RegisterShutdownCallback(OnShutdown);
            GameserverSDK.RegisterHealthCallback(IsHealthy);
            GameserverSDK.RegisterMaintenanceCallback(OnMaintenanceScheduled);


            //Read arguments
            LogMessage("Arguments are: " + string.Join(" ", args));

            // Read our asset file
            if (File.Exists(AssetFilePath))
            {
                _assetFileText = File.ReadAllText(AssetFilePath);
            }

            IDictionary<string, string> initialConfig = GameserverSDK.getConfigSettings();

            LogMessage("InitialConfig before ReadyForPlayers: \n" + JsonConvert.SerializeObject(initialConfig, Formatting.Indented));

            // Start the http server
            if (initialConfig?.ContainsKey(ListeningPortKey) == true)
            {
                int listeningPort = int.Parse(initialConfig[ListeningPortKey]);
                string address = $"http://*:{listeningPort}/";
                _listener.Prefixes.Add(address);
                _listener.Start();
            }
            else
            {
                LogMessage($"Cannot find {ListeningPortKey} in GSDK Config Settings. Please make sure the MockAgent is running " +
                           $"and that the MultiplayerSettings.json file includes {ListeningPortKey} as a GamePort Name.");
                return;
            }

            // Load our game certificate if it was installed
            if (initialConfig?.ContainsKey(GameCertAlias) == true)
            {
                string expectedThumbprint = initialConfig[GameCertAlias];
                X509Store store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadOnly);
                X509Certificate2Collection certificateCollection = store.Certificates.Find(X509FindType.FindByThumbprint, expectedThumbprint, false);

                if (certificateCollection.Count > 0)
                {
                    _installedCertThumbprint = certificateCollection[0].Thumbprint;
                }
                else
                {
                    LogMessage("Could not find installed game cert in LocalMachine\\My. Expected thumbprint is: " + expectedThumbprint);
                }
            }
            else
            {
                LogMessage("Config did not contain cert! Config is: " + string.Join(";", initialConfig.Select(x => x.Key + "=" + x.Value)));
            }

            Thread t = new Thread(ProcessRequests);
            t.Start();

            if (GameserverSDK.ReadyForPlayers())
            {
                _isActivated = true;

                // After allocation, we can grab the session cookie from the config
                IDictionary<string, string> activeConfig = GameserverSDK.getConfigSettings();

                if (activeConfig.TryGetValue(GameserverSDK.SessionCookieKey, out string sessionCookie))
                {
                    LogMessage($"The session cookie from the allocation call is: {sessionCookie}");
                }

                if (activeConfig.TryGetValue(GameserverSDK.SessionIdKey, out string sessionId))
                {
                    LogMessage($"The session Id from the allocation call is: {sessionId}");

                    //Get Title Entity Token
                    var entityToken = await PlayFabAuthenticationAPI.GetEntityTokenAsync(new PlayFab.AuthenticationModels.GetEntityTokenRequest());

                    if (entityToken.Error == null)
                    {
                        var matchResult = await PlayFabMultiplayerAPI.GetMatchAsync(new PlayFab.MultiplayerModels.GetMatchRequest { MatchId = sessionId, QueueName = "4PlayersTest", ReturnMemberAttributes = true });
                        if (matchResult.Error == null)
                        {
                            LogMessage(JsonConvert.SerializeObject(matchResult.Result, Formatting.Indented));
                            matchPlayers = matchResult.Result.Members.Select(m => m.Entity.Id).ToList();
                            LogMessage($"MatchPlayers are: {string.Join(", ", matchPlayers)}");
                        }
                        else
                        {
                            LogMessage($"matchResult error: {matchResult.Error.GenerateErrorReport()}");
                        }
                    }
                    else
                    {
                        LogMessage($"entityToken error: {entityToken.Error.GenerateErrorReport()}");
                    }
                }

                LogMessage("ActiveConfig after ReadyForPlayers: \n" + JsonConvert.SerializeObject(activeConfig, Formatting.Indented));

                var initialPlayers = GameserverSDK.GetInitialPlayers();

                LogMessage($"In this match there are {initialPlayers.Count} players. They are:");
                foreach (var item in initialPlayers)
                {
                    LogMessage(item);
                }
            }
            else
            {
                // No allocation happened, the server is getting terminated (likely because there are too many already in standing by)
                LogMessage("Server is getting terminated.");
            }
        }

        /// <summary>
        /// Listens for any requests and responds with the game server's config values
        /// </summary>
        private static void ProcessRequests()
        {
            while (_listener.IsListening)
            {
                try
                {
                    HttpListenerContext context = _listener.GetContext();
                    HttpListenerRequest request = context.Request;
                    HttpListenerResponse response = context.Response;

                    string requestMessage = $"HTTP:Received {request.Headers.ToString()}";
                    LogMessage(requestMessage);

                    var initialPlayers = GameserverSDK.GetInitialPlayers();
                    LogMessage($"There are {initialPlayers.Count} initialPlayers. They are: {string.Join(", ", initialPlayers)}");

                    var playerId = request.Headers.GetValues("token")?[0];
                    LogMessage($"playerId got from http header: {playerId}");

                    if (playerId == null || !matchPlayers.Contains(playerId))
                    {
                        response.AddHeader("token", playerId);
                        response.StatusCode = 403;
                        response.Close();
                        continue;
                    }

                    players.Add(new ConnectedPlayer(playerId));
                    GameserverSDK.UpdateConnectedPlayers(players);

                    requestCount++;
                    LogMessage($"Player count: {players.Count}. Current request count: {requestCount}.");

                    IDictionary<string, string> config = null;

                    config = GameserverSDK.getConfigSettings() ?? new Dictionary<string, string>();

                    config.Add("isActivated", _isActivated.ToString());
                    config.Add("assetFileText", _assetFileText);
                    config.Add("logsDirectory", GameserverSDK.GetLogsDirectory());
                    config.Add("installedCertThumbprint", _installedCertThumbprint);

                    config.Add("InitialPlayers", string.Join(", ", matchPlayers));
                    config.Add("ConnectedPlayers", string.Join(", ", players.Select(p => p.PlayerId)));

                    if (_nextMaintenance != DateTimeOffset.MinValue)
                    {
                        config.Add("nextMaintenance", _nextMaintenance.ToLocalTime().ToString());
                    }

                    string content = JsonConvert.SerializeObject(config, Formatting.Indented);

                    response.AddHeader("Content-Type", "application/json");
                    byte[] buffer = System.Text.Encoding.UTF8.GetBytes(content);
                    response.ContentLength64 = buffer.Length;
                    using (System.IO.Stream output = response.OutputStream)
                    {
                        output.Write(buffer, 0, buffer.Length);
                    }
                }
                catch (HttpListenerException httpEx)
                {
                    // This one is expected if we stopped the listener because we were asked to shutdown
                    LogMessage($"Got HttpListenerException: {httpEx.ToString()}, we are being shut down.");
                }
                catch (Exception ex)
                {
                    LogMessage($"Got Exception: {ex.ToString()}");
                }
            }
        }

        private static void LogMessage(string message, bool enableGSDKLogging = true)
        {
            Console.WriteLine(message);
            if (enableGSDKLogging)
            {
                GameserverSDK.LogMessage(message);
            }
        }
    }
}
