using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Emby.Webhooks.Configuration;
using System.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Common.Net;
using System.Net.Http;
using MediaBrowser.Controller.Notifications;
using System.Threading;
using MediaBrowser.Controller.Entities;

namespace Emby.Webhooks
{

    public class Webhooks : IServerEntryPoint
    {
        private readonly ISessionManager _sessionManager;
        private readonly IUserDataManager _userDataManager;
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IHttpClient _httpClient;
        private readonly INetworkManager _networkManager;
        private readonly IServerApplicationHost _appHost;

        private List<PauseControl> pauseControl = new List<PauseControl>();
        public class PauseControl
        {
            public string deviceId { get; set; }
            public bool wasPaused { get; set; }
        }

        public PauseControl getPauseControl(string deviceId)
        {
            var c = pauseControl.Where(x => x.deviceId == deviceId).FirstOrDefault();
            if (c == null)
            {
                c = new PauseControl() { deviceId = deviceId };
                pauseControl.Add(c);
            }
            return c;
        }

        public static Webhooks Instance { get; private set; }

        public string Name
        {
            get { return "Webhooks"; }
        }

        public Webhooks(ISessionManager sessionManager, IHttpClient httpClient, ILogManager logManager, IUserDataManager userDataManager, ILibraryManager libraryManager, INetworkManager networkManager, IServerApplicationHost appHost)
        {
            _logger = logManager.GetLogger(Plugin.Instance.Name);
            _libraryManager = libraryManager;
            _sessionManager = sessionManager;
            _userDataManager = userDataManager;
            _httpClient = httpClient;
            _networkManager = networkManager;
            _appHost = appHost;

            Instance = this;
        }

        public void Dispose()
        {
            //Unbind events
            _sessionManager.PlaybackStart -= PlaybackStart;
            _sessionManager.PlaybackStopped -= PlaybackStopped;
            _sessionManager.PlaybackProgress -= PlaybackProgress;

            _libraryManager.ItemAdded -= ItemAdded;
        }

        public void Run()
        {
            _sessionManager.PlaybackStart += PlaybackStart;
            _sessionManager.PlaybackStopped += PlaybackStopped;
            _sessionManager.PlaybackProgress += PlaybackProgress;

            _libraryManager.ItemAdded += ItemAdded;
        }

        private void ItemAdded(object sender, ItemChangeEventArgs e)
        {
            //Only concerned with video and audio files
            if (
                e.Item.IsVirtualItem == false &&
                (e.Item.MediaType == "Video" || e.Item.MediaType == "Audio")
                )
            {
                var iType = _libraryManager.GetContentType(e.Item);
                var hooks = hooksByType(iType).Where(h => h.onItemAdded);

                if (hooks.Count() > 0)
                {
                    _logger.Debug("{0} webhooks for item added events", hooks.Count().ToString());

                    foreach (var h in hooks)
                    {
                        SendHook(h, buildJson_Added(h, e.Item, "media.added"));
                    }
                }
            }
        }
        private void PlaybackProgress(object sender, PlaybackProgressEventArgs e)
        {
            var iType = _libraryManager.GetContentType(e.Item);

            if (e.IsPaused & getPauseControl(e.DeviceId).wasPaused == false)
            {
                PlaybackPause(sender, e);
            }
            else if (e.IsPaused == false & getPauseControl(e.DeviceId).wasPaused)
            {
                PlaybackResume(sender, e);
            }

        }

        private void PlaybackPause(object sender, PlaybackProgressEventArgs e)
        {
            //getPauseControl(e.DeviceId).wasPaused = true;

            var iType = _libraryManager.GetContentType(e.Item);
            var hooks = hooksByType(iType).Where(i => i.onPause);

            if (hooks.Count() > 0)
            {
                _logger.Debug("{0} webhooks for playback pause events", hooks.Count().ToString());

                foreach (var h in hooks)
                {
                    SendHook(h, buildJson_Playback(h, _sessionManager.GetSession(e.DeviceId.ToString(), e.ClientName, ""), "media.pause"));
                }
            }
        }

        private void PlaybackResume(object sender, PlaybackProgressEventArgs e)
        {
            //getPauseControl(e.DeviceId).wasPaused = false;

            var iType = _libraryManager.GetContentType(e.Item);
            var hooks = hooksByType(iType).Where(i => i.onResume);

            if (hooks.Count() > 0)
            {
                _logger.Debug("{0} webhooks for playback resume events", hooks.Count().ToString());

                foreach (var h in hooks)
                {
                    SendHook(h, buildJson_Playback(h, _sessionManager.GetSession(e.DeviceId.ToString(), e.ClientName, ""), "media.resume"));
                }
            }
        }

        private void PlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            //getPauseControl(e.DeviceId).wasPaused = false;

            var iType = _libraryManager.GetContentType(e.Item);
            var hooks = hooksByType(iType).Where(i => i.onPlay);

            _logger.Debug("{0} webhooks for playback start events", hooks.Count().ToString());

            if (hooks.Count() > 0)
            {
                _logger.Debug("{0} webhooks for playback start events", hooks.Count().ToString());

                foreach (var h in hooks)
                {
                    SendHook(h, buildJson_Playback(h, _sessionManager.GetSession(e.DeviceId.ToString(), e.ClientName, ""), "media.play"));
                }
            }
        }

        private void PlaybackStopped(object sender, PlaybackProgressEventArgs e)
        {
            //getPauseControl(e.DeviceId).wasPaused = false;

            var iType = _libraryManager.GetContentType(e.Item);
            var hooks = hooksByType(iType).Where(i => i.onStop);

            if (hooks.Count() > 0)
            {
                _logger.Debug("{0} webhooks for playback stop events", hooks.Count().ToString());

                foreach (var h in hooks) {
                    SendHook(h, buildJson_Playback(h, _sessionManager.GetSession(e.DeviceId.ToString(), e.ClientName, ""), "media.stop"));
                }
            }
        }

        public IEnumerable<PluginConfiguration.Hook> hooksByType(string type)
        {
            return Plugin.Instance.Configuration.Hooks.Where(h =>
                    (h.withMovies && type == "movies") ||
                    (h.withEpisodes && type == "tvshows") ||
                    (h.withSongs && type == "music")
            );
        }

        public async Task<bool> SendHook(PluginConfiguration.Hook h, string jsonString)
        {
            _logger.Debug("Sending paylod to {0}", h.URL);
            _logger.Debug(jsonString);

            using (var client = new HttpClient())
            {
                    var httpContent = new StringContent(jsonString, System.Text.Encoding.UTF8, "application/json");
                    var response = await client.PostAsync(h.URL, httpContent);
                    var responseString = await response.Content.ReadAsStringAsync();
                    _logger.Debug(response.StatusCode.ToString());
            }
            return true;
        }

        public string buildJson_Added(PluginConfiguration.Hook hooks, BaseItem e, string trigger)
        {
            string msgAdded = hooks.msgAdded;

            return msgAdded.Replace("{Event}", testString(trigger, true)).
            Replace("{ServerID}", testString(_appHost.SystemId, true)).
            Replace("{ServerName}", testString(_appHost.FriendlyName, true)).

            Replace("{ItemType}", testString(_libraryManager.GetContentType(e), true)).
            Replace("{ItemName}", testString(e.Name, true)).
            Replace("{ItemNameParent}", testString(e.Parent.Name, true)).
            Replace("{ItemNameGrandparent}", testString(e.Parent.Parent.Name, true)).
            Replace("{ItemID}", testString(e.Id.ToString(), true)).
            Replace("{ItemRunTimeTicks}", testString(e.RunTimeTicks.ToString(), false)).
            Replace("{ItemIndex}", testString(e.IndexNumber.ToString(), false)).
            Replace("{ItemParentIndex}", testString(e.ParentIndexNumber.ToString(), false)).

            Replace("{TimeStamp}", testString(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), true));
        }

        public string buildJson_Playback(PluginConfiguration.Hook hooks, SessionInfo e, string trigger)
        {
            string msgPlayback = hooks.msgPlayback;

            return msgPlayback.Replace("{Event}", testString(trigger, true)).
            Replace("{ServerID}", testString(_appHost.SystemId, true)).
            Replace("{ServerName}", testString(_appHost.FriendlyName, true)).
            Replace("{UserID}", testString(e.UserId.ToString(), true)).
            Replace("{UserName}", testString(e.UserName, true)).
            Replace("{AppName}", testString(e.Client, true)).
            Replace("{DeviceID}", testString(e.DeviceId.ToString(), true)).
            Replace("{DeviceName}", testString(e.DeviceName, true)).
            Replace("{DeviceIP}", testString(e.RemoteEndPoint.ToString(), true)).
            Replace("{ItemType}", testString(_libraryManager.GetContentType(e.FullNowPlayingItem), true)).
            Replace("{ItemName}", testString(e.FullNowPlayingItem.Name, true)).
            Replace("{ItemNameParent}", testString(e.FullNowPlayingItem.Parent.Name, true)).
            Replace("{ItemNameGrandparent}", testString(e.FullNowPlayingItem.Parent.Parent.Name, true)).
            Replace("{ItemID}", testString(e.FullNowPlayingItem.Id.ToString(), true)).
            Replace("{ItemRunTimeTicks}", testString(e.FullNowPlayingItem.RunTimeTicks.ToString(), false)).
            Replace("{ItemIndex}", testString(e.FullNowPlayingItem.IndexNumber.ToString(), false)).
            Replace("{ItemParentIndex}", testString(e.FullNowPlayingItem.ParentIndexNumber.ToString(), false)).
            Replace("{SessionID}", testString(e.Id, true)).
            Replace("{SessionPositionTicks}", testString(e.PlayState.PositionTicks.ToString(), false)).
            Replace("{TimeStamp}", testString(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), true));
        }

        public string testString(string inStr, bool isString)
        {
            if (string.IsNullOrEmpty(inStr))
            {
                if (isString) return "\"\"";

                return "0";
            }
            else {
                if (isString) return "\"" + inStr + "\"";

                return inStr;
            }
        }
    }
}
