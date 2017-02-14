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
using MediaBrowser.Controller.Entities;
using System.Threading;

namespace Emby.Webhooks
{
   
    public class Webhooks : IServerEntryPoint, INotificationService
    {
        private readonly ISessionManager _sessionManager;
        private readonly IUserDataManager _userDataManager;
        private readonly ILogger _logger;
        private readonly IJsonSerializer _jsonSerializer;

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

        public Webhooks(ISessionManager sessionManager, IJsonSerializer jsonSerializer, IHttpClient httpClient, ILogManager logManager, IUserDataManager userDataManager)
        {
            _logger = logManager.GetLogger(Plugin.Instance.Name);

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
        }
    

        public void Run()
        {
            _sessionManager.PlaybackStart += PlaybackStart;
            _sessionManager.PlaybackStopped += PlaybackStopped;
            _sessionManager.PlaybackProgress += PlaybackProgress;
        }

        private void PlaybackProgress(object sender, PlaybackProgressEventArgs e)
        {
            if (e.IsPaused & getPauseControl(e.DeviceId).wasPaused == false) {
                //Paused Event
                getPauseControl(e.DeviceId).wasPaused = true;

                var hooks = Plugin.Instance.Configuration.Hooks.Where
                    (i => i.onPause & (
                            (i.withMovies & e.MediaInfo.Type == "Movie")
                            || (i.withEpisodes & e.MediaInfo.Type == "Episode")
                            || (i.withSongs & e.MediaInfo.Type == "Song")
                           )
                );


                var jsonString = buildJson(e, "media.pause");

                SendHooks(hooks, jsonString);
            }else if (e.IsPaused == false & getPauseControl(e.DeviceId).wasPaused)
            {
                getPauseControl(e.DeviceId).wasPaused = false;

                var hooks = Plugin.Instance.Configuration.Hooks.Where
                    (i => i.onResume & (
                            (i.withMovies & e.MediaInfo.Type == "Movie")
                            || (i.withEpisodes & e.MediaInfo.Type == "Episode")
                            || (i.withSongs & e.MediaInfo.Type == "Song")
                           )
                );
                var jsonString = buildJson(e, "media.resume");

                SendHooks(hooks, jsonString);
            }

        }

        private void PlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            getPauseControl(e.DeviceId).wasPaused = false;

            var jsonString = buildJson(e, "media.play");
            //get all configured hooks for onPlay
            var hooks = Plugin.Instance.Configuration.Hooks.Where
                (i => i.onPlay & (
                            (i.withMovies & e.MediaInfo.Type == "Movie")
                            || (i.withEpisodes & e.MediaInfo.Type == "Episode")
                            || (i.withSongs & e.MediaInfo.Type == "Song")
                           )   
                 );

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
                            || (i.withSongs & e.MediaInfo.Type == "Song")
                           )
                );
            if (hooks.Count() > 0)
            {
                _logger.Debug("{0} webhooks for pause events", hooks.Count().ToString());
                var jsonString = buildJson(e, "media.stop");
                SendHooks(hooks, jsonString);
            }
         }

        public void SendHooks(IEnumerable<PluginConfiguration.Hook> hooks, string jsonString)
        {
            foreach (var h in hooks)
            {
                //send payload
                SendHooks(h, jsonString);

            }
        }

        public async Task<bool> SendHooks (PluginConfiguration.Hook h, string jsonString)
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
        

        public string buildJson (PlaybackProgressEventArgs e, string trigger)
        {
            envelope j = new envelope() {
                   _event = trigger,

                   Account = new Account() { },
                   Player = new Player() {
                       title = e.ClientName,
                       uuid = e.DeviceId.ToString()
                   },
                   Metadata = new Metadata()
                   {
                       type = e.MediaInfo.Type,
                       title = e.MediaInfo.Name,
                       grandparentTitle = e.Item.Parent.Parent.Name,
                       parentTitle = e.Item.Parent.Name,
                       guid = e.Item.Id.ToString()
                   }
            };

            return _jsonSerializer.SerializeToString(j);

            /*
            MemoryStream stream1 = new MemoryStream();
            DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(envelope));
            ser.WriteObject(stream1, j);

            stream1.Position = 0;
            StreamReader sr = new StreamReader(stream1);
            

            return (sr.ReadToEnd());
            */
        }

        public Task SendNotification(UserNotification request, CancellationToken cancellationToken)
        {
            return SendNotifcationHook(request);
        }

        public async Task<bool> SendNotifcationHook(UserNotification request)
        {
            var hooks = Plugin.Instance.Configuration.Hooks.Where(i => i.withNotifications);
            var json = _jsonSerializer.SerializeToString(request);
            _logger.Debug(json);

            foreach (var h in hooks)
            {
                await SendHooks(h, json);
            }
            return true;
        }

        public bool IsEnabledForUser(User user)
        {

            return user.Policy.IsAdministrator;
        }
    }
}
