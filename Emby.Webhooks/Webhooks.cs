using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Emby.Webhooks.Configuration;
using System.Runtime.Serialization.Json;
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
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ILibraryManager _libraryManager;

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
            get{ return "Webhooks";}
        }

        public Webhooks(ISessionManager sessionManager, IJsonSerializer jsonSerializer, IHttpClient httpClient, ILogManager logManager, IUserDataManager userDataManager, ILibraryManager libraryManager)
        {
            _logger = logManager.GetLogger(Plugin.Instance.Name);
            _libraryManager = libraryManager;
            _sessionManager = sessionManager;
            _userDataManager = userDataManager;
            _jsonSerializer = jsonSerializer;

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

        private void ItemAdded(object sender, ItemChangeEventArgs e) {
            _logger.Debug("Item added event");
            _logger.Debug(_jsonSerializer.SerializeToString(e));

            var cType = _libraryManager.GetContentType(e.Item);

            //Only concerned with video and audio files
            if (
                e.Item.IsVirtualItem == false &&
                (e.Item.MediaType == "Video" || e.Item.MediaType == "Audio")
                ) {
                var hooks = hooksByType(cType).Where(h => h.onItemAdded);
           
                if (hooks.Count() > 0)
                {
                    var jsonString = buildJson(e.Item, "media.added");
                    SendHooks(hooks, jsonString);
                }
            }
        }
        private void PlaybackProgress(object sender, PlaybackProgressEventArgs e)
        {
            var iType = _libraryManager.GetContentType(e.Item);

            if (e.IsPaused & getPauseControl(e.DeviceId).wasPaused == false)
            {
                _logger.Debug("Playback Paused event");
                _logger.Debug(_jsonSerializer.SerializeToString(e));

                //Paused Event
                getPauseControl(e.DeviceId).wasPaused = true;

                var hooks = hooksByType(iType).Where(i => i.onPause);

                var jsonString = buildJson(e, "media.pause");

                SendHooks(hooks, jsonString);
            }
            else if (e.IsPaused == false & getPauseControl(e.DeviceId).wasPaused)
            {
                _logger.Debug("Playback Resume event");
                _logger.Debug(_jsonSerializer.SerializeToString(e));


                getPauseControl(e.DeviceId).wasPaused = false;

                var hooks = hooksByType(iType).Where(i => i.onResume);
                var jsonString = buildJson(e, "media.resume");

                SendHooks(hooks, jsonString);
            }

        }
        private void PlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            _logger.Debug("Playback Start event");
            _logger.Debug(_jsonSerializer.SerializeToString(e));

            getPauseControl(e.DeviceId).wasPaused = false;

            var iType = _libraryManager.GetContentType(e.Item);

            //get all configured hooks for onPlay
            var hooks = hooksByType(iType).Where(i => i.onPlay);

            var jsonString = buildJson(e, "media.play");

            SendHooks(hooks, jsonString);
        }
        private void PlaybackStopped(object sender, PlaybackProgressEventArgs e)
        {
            getPauseControl(e.DeviceId).wasPaused = false;

            //_logger.Info(_jsonSerializer.SerializeToString(e));

            //get all configured hooks for onPlay
            var hooks = Plugin.Instance.Configuration.Hooks.Where
                (i => i.onStop & (
                            (i.withMovies & e.MediaInfo.Type == "Movie")
                            || (i.withEpisodes & e.MediaInfo.Type == "Episode")
                            || (i.withSongs & e.MediaInfo.Type == "Audio")
                           )
                );
            if (hooks.Count() > 0)
            {
                _logger.Debug("{0} webhooks for pause events", hooks.Count().ToString());
                var jsonString = buildJson(e, "media.stop");
                SendHooks(hooks, jsonString);
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
        
        public void SendHooks(IEnumerable<PluginConfiguration.Hook> hooks, string jsonString)
        {
            foreach (var h in hooks)
            {
                //send payload
                SendHook(h, jsonString);

            }
        }
       
        public async Task<bool> SendHook (PluginConfiguration.Hook h, string jsonString)
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

        public string buildJson(BaseItem i, string trigger)
        {

            envelope j = new envelope()
            {
                @event = trigger,

                Metadata = new Metadata()
                {
                    type = _libraryManager.GetContentType(i),
                    title = i.Name,
                    grandparentTitle = i.Parent.Parent.Name,
                    parentTitle = i.Parent.Name,
                    guid = i.Id.ToString()
                }
            };
            return _jsonSerializer.SerializeToString(j);
        }

        public string buildJson (PlaybackProgressEventArgs e, string trigger)
        {
            // User u = e.Users.FirstOrDefault();

            envelope j = new envelope() {
                @event = trigger,

                Account = new Account() { },
                Player = new Player() {
                    title = e.ClientName,
                    uuid = e.DeviceId.ToString()
                },
                Metadata = new Metadata()
                {
                    type = _libraryManager.GetContentType(e.Item),
                    title = e.Item.Name,
                    grandparentTitle = e.Item.Parent.Parent.Name,
                    parentTitle = e.Item.Parent.Name,
                    guid = e.Item.Id.ToString()
                    
                   }
            };

            return _jsonSerializer.SerializeToString(j);
        }
    }
}
