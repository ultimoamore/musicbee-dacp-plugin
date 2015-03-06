using System;
using Melloware.DACP;
using System.IO;
using log4net.Config;
using log4net;
using System.Windows.Forms;
using ZeroconfService;
using System.Text;
using System.Collections.Generic;
using System.Collections;
using System.Xml.Linq;
using System.Net;
using System.Linq;
using DacpMusicBeePlugin;
using System.Text.RegularExpressions;
using System.Threading;

namespace MusicBeePlugin
{
    public partial class Plugin : DACPServer
    {
        public const string APPLICATION_NAME = "MusicBee";
        private const int PLAYLIST_LIBRARY_ID = 99999999;
        private const int PLAYLIST_MUSIC_ID = 99999998;
        private const int PLAYLIST_DJ_ID = 99999997;
        private const int DATABASE_ID = 1;

        private static MusicBeeApiInterface mbApi;
        private PluginInfo about = new PluginInfo();

        private ManualResetEventSlim artworkDownloaded = new ManualResetEventSlim();

        private Lazy<MusicBeeLibCache> cache = new Lazy<MusicBeeLibCache>(() => 
        {
            return new MusicBeeLibCache(mbApi);
        });

        // logger
		private static readonly ILog LOG = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly System.Timers.Timer timer = new System.Timers.Timer(250);
        private static Boolean isRewinding = false;
        
        private bool processMBEvents = true; 
        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            try
            {
                mbApi = new MusicBeeApiInterface();
                mbApi.Initialise(apiInterfacePtr);
                about.PluginInfoVersion = PluginInfoVersion;
                about.Name = "DACP Server";
                about.Description = "Allows DACP remote control";
                about.Author = "Leonardo Francalanci";
                about.TargetApplication = "MusicTunes";
                about.Type = PluginType.General;
                about.VersionMajor = 1;  // your plugin version
                about.VersionMinor = 0;
                about.Revision = 0;
                about.MinInterfaceVersion = MinInterfaceVersion;
                about.MinApiRevision = MinApiRevision;
                about.ReceiveNotifications = ReceiveNotificationFlags.DataStreamEvents | ReceiveNotificationFlags.PlayerEvents 
                                            | ReceiveNotificationFlags.TagEvents;
                about.ConfigurationPanelHeight = 0;   // not implemented yet: height in pixels that musicbee should reserve in a panel for config settings. When set, a handle to an empty panel will be passed to the Configure function

                // Hook up the Elapsed event for the timer.
                timer.Elapsed += new System.Timers.ElapsedEventHandler(OnTimedEvent);

                // TODO ??? 
                string log4netFile = mbApi.Setting_GetPersistentStoragePath() + "\\log4net.xml";
                FileInfo configFile = new System.IO.FileInfo(log4netFile);
                XmlConfigurator.ConfigureAndWatch(configFile);
                LOG.InfoFormat("*** Creating MusicBee DACP Server {0} ***", this.Version);

                try
                {
                    base.Start();
                }
                catch (DACPBonjourException)
                {
                    MessageBox.Show("Bonjour is required by this application and it was not found.  A browser will be opened to a download page.  Please install Bonjour For Windows", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    System.Diagnostics.Process.Start("http://support.apple.com/downloads/Bonjour_for_Windows");
                }
                catch (Exception ex)
                {
                    LOG.Error(this.GetApplicationName() + " Server Error: " + ex.Message, ex);
                    MessageBox.Show(this.GetApplicationName() + " Error: " + ex.Message, this.GetApplicationName() + " Server Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                LOG.Error("MusicBee DACP Server Error: " + ex.Message, ex);
            }

            return about;
        }

        // TODO move?
        public bool Configure(IntPtr panelHandle)
        {
            // save any persistent settings in a sub-folder of this path
            string dataPath = mbApi.Setting_GetPersistentStoragePath();
            return false;
        }

        // TODO move?
        // called by MusicBee when the user clicks Apply or Save in the MusicBee Preferences screen.
        // its up to you to figure out whether anything has changed and needs updating
        public void SaveSettings()
        {
            // save any persistent settings in a sub-folder of this path
            string dataPath = mbApi.Setting_GetPersistentStoragePath();
        }

        // MusicBee is closing the plugin (plugin is being disabled by user or MusicBee is shutting down)
        public void Close(PluginCloseReason reason)
        {
            LOG.Info("Shutting Down MusicBee DACP Server...");
            try
            {
                base.Stop();
            }
            catch (Exception ex)
            {
                LOG.Error("MusicBee DACP Server Stop: " + ex.Message, ex);
            }
        }

        // TODO
        // uninstall this plugin - clean up any persisted files
        public void Uninstall()
        {
            try
            {
                File.Delete(GetFileName());
            }
            catch
            { }
        }
 
        private string GetUserDataPath() {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            dir = System.IO.Path.Combine(dir, GetApplicationName());
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// Gets the filename for this pairing database file based on app name.
        ///
        /// Ex: C:\Users\USER\AppData\Roaming\DACP\dacp.xml
        /// </summary>
        /// <returns>a file name to the database path</returns>
        private string GetFileName() {
            return GetUserDataPath() + "\\" + GetApplicationName() + ".xml";
        }

        public override void OnClientListChanged()
        {
            Dictionary<string, NetService> temp = new Dictionary<string, NetService>(PairingServer.Services);
            foreach (KeyValuePair<string, NetService> pair in temp)
            {
                string deviceName;
                NetService service = pair.Value;
                try
                {
                    byte[] txt = service.TXTRecordData;
                    IDictionary dict = NetService.DictionaryFromTXTRecordData(txt);
                    byte[] value = (byte[])dict["DvNm"];
                    deviceName = Encoding.UTF8.GetString(value);

                }
                catch (Exception)
                {
                    deviceName = "Unknown Device";
                }
                DACPPairingForm form = new DACPPairingForm();
                if (form.ShowDialog() == DialogResult.OK)
                {
                    string passCode = form.GetPassCode();
                    LOG.DebugFormat("PassCode = {0}", passCode);
                    PairingServer.PairService(service, passCode);
                    DACPServer.PairingDatabase.Store();
                }
            }
        }

        // receive event notifications from MusicBee
        // you need to set about.ReceiveNotificationFlags = PlayerEvents to receive all notifications, and not just the startup event
        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            switch (type)
            {
                case NotificationType.NowPlayingArtworkReady:
                    artworkDownloaded.Set();
                    break;
                case NotificationType.AutoDjStarted:
                case NotificationType.AutoDjStopped:
                case NotificationType.NowPlayingListChanged:
                case NotificationType.NowPlayingListEnded:
                case NotificationType.PlayCountersChanged:
                case NotificationType.PlayerRepeatChanged:
                case NotificationType.PlayerScrobbleChanged:
                case NotificationType.PlayerShuffleChanged:
                case NotificationType.PlayStateChanged:
                case NotificationType.ReplayGainChanged:
                case NotificationType.TrackChanged:
                case NotificationType.VolumeLevelChanged:
                case NotificationType.VolumeMuteChanged:
                    if (processMBEvents)
                        ReleaseAllLatches();
                    break;
            }
        }

        public override string GetApplicationName()
        {
            return APPLICATION_NAME;
        }


		/// <summary>
		/// Event triggered when the fast forward or rewind timer starts.
		/// </summary>
		/// <param name="sender">the sender of the event</param>
		/// <param name="e">the ElapsedEventArgs arguments</param>
		private void OnTimedEvent(object sender, System.Timers.ElapsedEventArgs e) {
			LOG.DebugFormat("Timer Tick");
			if ((mbApi.Player_GetPlayState() == PlayState.Playing)) {
                int pos = mbApi.Player_GetPosition();
                if (isRewinding)
                {
					if (pos - 5000 > 0) 
                    {
						mbApi.Player_SetPosition(pos - 5000);
					}
				} 
                else 
                {
					if (pos + 5000 < mbApi.NowPlaying_GetDuration()) 
                    {
                        mbApi.Player_SetPosition(pos + 5000);
                    }
				}
			}
		}


        protected override DACPResponse PlaylistAdd(System.Net.HttpListenerRequest request)
        {
            LOG.Debug("Adding New Playlist...");
            EditResponse editResponse = new EditResponse(request);
            try
            {
                string playlistName = editResponse.QueryParams[DACPResponse.PROPERTY_ITEMNAME];
                LOG.InfoFormat("Creating new playlist '{0}'", playlistName);
                // TODO folder name:
                string url = mbApi.Playlist_CreatePlaylist("", playlistName, null);
                Playlist pl = new Playlist();
                pl.Name = playlistName;
                pl.Url = url;
                pl.Id = cache.Value.playlists.Last().Id + 1;
                cache.Value.playlists.Add(pl);

                editResponse.Miid = pl.Id;
            }
            catch (Exception ex)
            {
                LOG.Error("Error Creating New Playlist...", ex);
            }

            return editResponse;
        }

        protected override DACPResponse PlaylistRemove(System.Net.HttpListenerRequest request)
        {
            LOG.Debug("Removing Playlist...");
            EditResponse editResponse = new EditResponse(request);
            try
            {
                int playlistId = Convert.ToInt32(editResponse.QueryParams[DACPResponse.PROPERTY_ITEMID]);
                LOG.InfoFormat("Removing playlist '{0}'", playlistId);
                Playlist pl = cache.Value.playlists.Find(p => p.Id == playlistId);
                if (pl != null)
                {
                    editResponse.Miid = pl.Id;
                    mbApi.Playlist_DeletePlaylist(pl.Url);
                }
            }
            catch (Exception ex)
            {
                LOG.Error("Error Removing Playlist...", ex);
            }

            return editResponse;
        }

        protected override DACPResponse PlaylistRename(System.Net.HttpListenerRequest request)
        {
            LOG.Warn("PlaylistRename DACP command not supported by MusicBee!");
            return null;
        }

        protected override DACPResponse PlaylistRefresh(System.Net.HttpListenerRequest request)
        {
            LOG.Warn("PlaylistRefresh DACP command not supported by MusicBee!");
            return null;
        }

        protected override DACPResponse PlaylistAddTrack(System.Net.HttpListenerRequest request)
        {
            LOG.Debug("Adding Track To Playlist...");
            EditResponse editResponse = new EditResponse(request);
            try
            {
                Playlist pl = cache.Value.playlists.Find(p => p.Id == editResponse.PlaylistId);
                if (pl != null)
                {
                    int trackId = Convert.ToInt32(editResponse.QueryParams[DACPResponse.PROPERTY_ITEMID]);
                    Track track;
                    if (cache.Value.tracksById.TryGetValue(trackId, out track))
                    {
                        LOG.DebugFormat("Adding Track {0} to Playlist '{1}'", trackId, pl.Name);
                        mbApi.Playlist_AppendFiles(pl.Url, new string[] { track.Url });
                        pl.Tracks.Add(track);
                        editResponse.AddEditNode(pl.Tracks.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                LOG.Error("Error Adding Track To Playlist...", ex);
            }
            return editResponse;
        }

        protected override DACPResponse PlaylistRemoveTrack(System.Net.HttpListenerRequest request)
        {
            LOG.Debug("Removing Track From Playlist...");
            EditResponse editResponse = new EditResponse(request);
            try
            {
                Playlist pl = cache.Value.playlists.Find(p => p.Id == editResponse.PlaylistId);
                int itemId = Convert.ToInt32(editResponse.QueryParams[DACPResponse.PROPERTY_CONTAINERITEMID]) - 1;
                if (pl != null)
                {
                    LOG.WarnFormat("Removing Track {0} from Playlist '{1}'", itemId, pl.Name);
                    mbApi.Playlist_RemoveAt(pl.Url, itemId);
                }
            }
            catch (Exception ex)
            {
                LOG.Error("Error Removing Track From Playlist...", ex);
            }
            return editResponse;
        }

        protected override DACPResponse PlaylistMoveTrack(System.Net.HttpListenerRequest request)
        {
            LOG.Debug("Moving Track In Playlist...");
            EditResponse editResponse = new EditResponse(request);
            try
            {
                //TODO???
                string moveTrackList = editResponse.QueryParams[DACPResponse.PROPERTY_EDITPARAM_MOVEPAIR];
                LOG.InfoFormat("Moving Tracks: {0}", moveTrackList);
            }
            catch (Exception ex)
            {
                LOG.Error("Error Moving Track In Playlist...", ex);
            }
            return editResponse;
        }

        protected override DACPResponse GetAlbums(System.Net.HttpListenerRequest request)
        {
            LOG.Info("Gettings Albums...");
            AlbumsResponse albumsResponse = new AlbumsResponse(request);
            try
            {
                string[] artistName = albumsResponse.QueryParams.GetValues(DACPResponse.PROPERTY_ARTISTNAME);
                string[] albumName = albumsResponse.QueryParams.GetValues(DACPResponse.PROPERTY_ALBUMNAME);
                string[] genreName = albumsResponse.QueryParams.GetValues(DACPResponse.PROPERTY_GENRE);
                string[] composerName = albumsResponse.QueryParams.GetValues(DACPResponse.PROPERTY_COMPOSER);
                string[] mediaKind = albumsResponse.QueryParams.GetValues(DACPResponse.PROPERTY_MEDIAKIND);
                LOG.DebugFormat("MediaKind = {0}", mediaKind);
                if ((mediaKind != null) && !(mediaKind.Contains(ITUNES_MEDIAKIND_MUSIC)))
                {
                    return albumsResponse;
                }

                List<Album> albums = null; 
                if (artistName != null)
                {
                    List<Album> albumsList = new List<Album>();
                    foreach (string artist in artistName)
                    {
                        Artist art;
                        if (cache.Value.artistMap.TryGetValue(artist, out art))
                            albumsList.AddRange(art.Albums);
                    }
                    albums = albumsList;
                }
                else
                {
                    albums = cache.Value.artistMap.Values.SelectMany(art => art.Albums).ToList();
                }
                
                if (genreName != null)
                {
                    albums = albums.Where(alb => alb.Tracks.Any(tr => genreName.Contains(tr.Genre))).ToList();
                }
                
                if (composerName != null)
                {
                    albums = albums.Where(alb => alb.Tracks.Any(tr => composerName.Contains(tr.Composer))).ToList();
                }
                if (albumName != null)
                {
                    bool useLike = IsLikeParameter(albumName);
                    if (useLike)
                        albums = albums.Where(alb => alb.Title.IndexOf(albumName[0], StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                    else
                        albums = albums.Where(alb => albumName.Contains(alb.Title)).ToList();
                }

                albums = albums.OrderBy(alb => alb.Title).ToList();
                if (albumsResponse.IsIndexed)
                    albums = albums.Skip(albumsResponse.StartIndex).Take(albumsResponse.EndIndex - albumsResponse.StartIndex).ToList();

                foreach(Album album in albums)
                {
                    albumsResponse.AddAlbumNode(album.Id, album.artist.Name, album.Title, album.Tracks.Count);
                }
            }
            catch (Exception ex)
            {
                LOG.Error("Error Getting Albums...", ex);
            }

            return albumsResponse;
        }

        protected override DACPResponse GetArtists(System.Net.HttpListenerRequest request)
        {
            LOG.Info("Browsing Artists...");
            BrowseResponse browseResponse = new BrowseResponse(request);
            ArtistsResponse artistResponse = new ArtistsResponse(request);
            DACPResponse response = artistResponse;
            // TODO bool isItunes = false;

            // use Artists reponse for Apple Remote, Browse for all others.
            if (request != null)
            {
                // if a /groups request return AGAR else return the older Browse response ABRO
                if (request.RawUrl.Contains("groups"))
                {
                    response = artistResponse;
                    // TODO isItunes = true;
                }
                else
                {
                    response = browseResponse;
                }
            }

            try
            {
                StringBuilder builder = new StringBuilder();
                string[] artistName = response.QueryParams.GetValues(DACPResponse.PROPERTY_ARTISTNAME);
                string[] genreName = response.QueryParams.GetValues(DACPResponse.PROPERTY_GENRE);

                List<Artist> artists = new List<Artist>();
                if (artistName != null)
                {
                    bool useLike = IsLikeParameter(artistName);
                    if (useLike)
                        artists = cache.Value.artistMap.Values.Where(art => art.Name.IndexOf(artistName[0], StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                    else
                    {
                        foreach (string artist in artistName)
                        {
                            Artist art;
                            if (cache.Value.artistMap.TryGetValue(artist, out art))
                                artists.Add(art);
                        }
                    }
                }
                else
                {
                    artists = cache.Value.artistMap.Values.ToList();
                }
                
                if (genreName != null)
                {
                    artists = artists.Where(art => art.Albums.Any(alb => genreName.Contains(alb.Tracks[0].Genre))).ToList();
                }

                artists = artists.OrderBy(art => art.Name).ToList();

                if (response.IsIndexed)
                    artists = artists.Skip(response.StartIndex).Take(response.EndIndex - response.StartIndex).ToList();

                foreach (Artist artist in artists)
                {
                    int albumCount = artist.Albums.Count;
                    int trackCount = 0;
                    artist.Albums.ForEach(a => trackCount += a.Tracks.Count);

                    browseResponse.AddArtist(artist.Name);
                    artistResponse.AddArtistNode(artist.Id, artist.Name, albumCount, trackCount);
                }
            }
            catch (Exception ex)
            {
                LOG.Error("Error Browsing Artists...", ex);
            }

            return response;
        }

        protected override DACPResponse GetArtworkResponse(System.Net.HttpListenerRequest request)
        {
            ArtworkResponse response = new ArtworkResponse(request);
            try
            {
                string artWorkString = null;
                byte[] artworkBytes = null;
                if (response.ItemId <= ArtworkResponse.NOW_PLAYING)
                {
                    artWorkString = mbApi.NowPlaying_GetArtwork();
                    if (artWorkString == null)
                    {
                        artworkDownloaded.Reset();
                        artWorkString = mbApi.NowPlaying_GetDownloadedArtwork();
                        if (artWorkString == null)
                        {
                            artworkDownloaded.Wait(5000);
                            artWorkString = mbApi.NowPlaying_GetDownloadedArtwork();
                        }
                    }  
                }
                else
                {
                    if (String.Equals("albums", response.GroupType)) 
                    {
                        Album album = cache.Value.FindAlbumById(response.ItemId);
                        if (album != null)
                            artWorkString = mbApi.Library_GetArtwork(album.Tracks[0].Url, 0);
                    }
                    else if (String.Equals("artist", response.GroupType) || String.Equals("artists", response.GroupType))
                    {
                        Artist artist = cache.Value.artistMap.Values.Where(art => art.Id == response.ItemId).FirstOrDefault();
                        if (artist != null)
                        {
                            string thumb = mbApi.Library_GetArtistPictureThumb(artist.Name);
                            if (thumb != null)
                            {
                                artworkBytes = File.ReadAllBytes(thumb);
                            }
                        }
                    }
                    else
                    {
                        //??
                    }
                }

                if (artWorkString != null)
                    response.AddArtwork(new MemoryStream(Convert.FromBase64String(artWorkString)));
                else if (artworkBytes != null)
                    response.AddArtwork(new MemoryStream(artworkBytes));
            }
            catch (Exception ex)
            {
                LOG.Error("Error Loading Artwork...", ex);
            }

            return response;
        }

        protected override DACPResponse GetComposers(System.Net.HttpListenerRequest request)
        {
            LOG.Info("Browsing Composers...");
            BrowseResponse browseResponse = new BrowseResponse(request);
            try
            {
                IEnumerable<string> composers = cache.Value.tracksByUrl.Values.Select(t => t.Composer).Distinct().OrderBy(s => s);

                // add the limit if a limit was sent in
                if (browseResponse.IsIndexed)
                    composers = composers.Skip(browseResponse.StartIndex).Take(browseResponse.EndIndex - browseResponse.StartIndex);

                foreach (string c in composers)
                    browseResponse.AddComposer(c);
            }
            catch (Exception ex)
            {
                LOG.Error("Error Browsing Composers...", ex);
            }
            return browseResponse;
        }

        protected override DACPResponse GetCurrentPlayerStatus(System.Net.HttpListenerRequest request)
        {
            PlayerStatusUpdateResponse response = new PlayerStatusUpdateResponse(request);
            try
            {
                string trackUrl = mbApi.NowPlaying_GetFileUrl();
                Track track;
                if (cache.Value.tracksByUrl.TryGetValue(trackUrl, out track))
                {
                    if (mbApi.Player_GetPlayState() == PlayState.Playing)
                    {
                        response.Caps = PlayerStatusUpdateResponse.PLAYING;
                        LOG.Debug("PlayerStatusUpdateResponse PLAYING");
                    }

                    // do two if's and not an if else because of the way MM works
                    // isPlaying == true even if Paused!
                    if (mbApi.Player_GetPlayState() == PlayState.Paused)
                    {
                        response.Caps = PlayerStatusUpdateResponse.PAUSED;
                        LOG.Debug("PlayerStatusUpdateResponse PAUSED");
                    }

                    // fill out the rest of the track info
                    response.Cmvo = Convert.ToInt32(mbApi.Player_GetVolume() * 100);
                    response.Cavs = DACPResponse.FALSE;
                    response.Cave = DACPResponse.FALSE;
                    response.Cash = (byte)(mbApi.Player_GetShuffle() ? 1 : 0);
                    if (mbApi.Player_GetRepeat() == RepeatMode.None)
                        response.Carp = (byte)0;
                    else if (mbApi.Player_GetRepeat() == RepeatMode.One)
                        response.Carp = (byte)1;
                    else 
                        response.Carp = (byte)2;
                    response.Cann = track.Title;
                    response.Cana = track.album.artist.Name;
                    response.Canl = track.album.Title;
                    response.Cang = track.Genre;
                    response.Asai = (ulong)track.album.Id;
                    response.Cant = mbApi.NowPlaying_GetDuration() - mbApi.Player_GetPosition();
                    response.Cast = mbApi.NowPlaying_GetDuration();

                    // media kind default to MUSIC
                    response.Cmmk = DACPResponse.MEDIAKIND_MUSIC;
                    // TODO
                    /*
                    if (MediaMonkey.VersionHi >= 4)
                    {
                        switch (track.TrackType)
                        {
                            case 0:
                            case 3:
                                response.Cmmk = DACPResponse.MEDIAKIND_MUSIC;
                                break;
                            case 1:
                                response.Cmmk = DACPResponse.MEDIAKIND_PODCAST;
                                break;
                            case 2:
                                response.Cmmk = DACPResponse.MEDIAKIND_AUDIOBOOK;
                                break;
                            case 4:
                            case 5:
                            case 6:
                            case 7:
                                response.Cmmk = DACPResponse.MEDIAKIND_VIDEO;
                                break;
                            default:
                                response.Cmmk = DACPResponse.MEDIAKIND_MUSIC;
                                break;
                        }
                    }
                    */

                    response.CurrentDatabase = DATABASE_ID; // TODO
                    response.CurrentPlaylist = PLAYLIST_LIBRARY_ID; // TODO???
                    response.CurrentPlaylistTrack = mbApi.NowPlayingList_GetCurrentIndex();
                    response.CurrentTrack = track.Id;
                }
            }
            catch (Exception ex)
            {
                LOG.Error("Error Getting Player Status...", ex);
            }
            return response;

        }

        protected override DACPResponse GetDatabaseInfo(System.Net.HttpListenerRequest request)
        {
            DatabaseResponse dbResponse = new DatabaseResponse(request);
            dbResponse.Minm = this.GetApplicationName();
            dbResponse.Miid = DATABASE_ID;
            dbResponse.Mper = (ulong)DATABASE_ID; 
            int playlistCount = cache.Value.playlists.Count(); 
            if (playlistCount == 0)
            {
                // default to 1 because the iPad requires at least one playlist
                playlistCount = 1;
            }
            dbResponse.Mctc = playlistCount;
            return dbResponse;

        }

        private bool IsLikeParameter(string[] vals)
        {
            const String likePattern = @"^[%|\*](?<query>.*)[%|\*]$";
            const String replacePattern = @"${query}";

            bool useLike = false;
            if (vals.Length == 1)
            {
                string v = Regex.Replace(vals[0], likePattern, replacePattern);
                if (v != vals[0])
                {
                    vals[0] = v;
                    useLike = true;
                }
            }

            return useLike;
        }

        protected override DACPResponse GetGenres(System.Net.HttpListenerRequest request)
        {
            LOG.Info("Browsing Genres...");
            BrowseResponse browseResponse = new BrowseResponse(request);
            try
            {
                IEnumerable<string> genres = cache.Value.tracksByUrl.Values.Select(t => t.Genre).Distinct();

                string[] genreNames = browseResponse.QueryParams.GetValues(DACPResponse.PROPERTY_GENRE);
                if (genreNames != null)
                {
                    bool useLike = IsLikeParameter(genreNames);

                    LOG.DebugFormat("GENRE: {0}", genreNames);
                    if (useLike)
                        genres = genres.Where(g => g.Contains(genreNames[0]));
                    else
                        genres = genres.Where(g => genreNames.Any(l => l == g));
                }

                genres = genres.OrderBy(s => s);
                // add the limit if a limit was sent in
                if (browseResponse.IsIndexed)
                    genres = genres.Skip(browseResponse.StartIndex).Take(browseResponse.EndIndex - browseResponse.StartIndex);

                foreach(string g in genres)
                    browseResponse.AddGenre(g);
            }
            catch (Exception ex)
            {
                LOG.Error("Error Browsing Genres...", ex);
            }

            return browseResponse;
        }

        protected override DACPResponse GetNowPlaying(System.Net.HttpListenerRequest request)
        {
            LOG.Debug("Getting now playing playlist...");
            TracksResponse tracksResponse = new TracksResponse(request);
            try
            {
                if (mbApi.NowPlayingList_QueryFiles(""))
                {
                    while (true)
                    {
                        string currentFile = mbApi.NowPlayingList_QueryGetNextFile();
                        if (String.IsNullOrEmpty(currentFile))
                            break;

                        Track track = null;
                        if (!cache.Value.tracksByUrl.TryGetValue(currentFile, out track))
                            continue;

                        TrackNode trackNode = convertSongToTrackNode(track);
                        tracksResponse.AddTrackNode(trackNode);
                    }
                }

                if (tracksResponse.Mlcl.Count == 0)
                {
                    LOG.WarnFormat("No Tracks were found for query: {0}", tracksResponse.GetQuery());
                }
            }
            catch (Exception ex)
            {
                LOG.Error("Error Getting Tracks...", ex);
            }

            return tracksResponse;

        }

        protected override DACPResponse GetPlaylists(System.Net.HttpListenerRequest request)
        {
            LOG.Info("Getting Playlists...");
            PlaylistsResponse playlistResponse = new PlaylistsResponse(request);
            try
            {
                int totalSongCount = cache.Value.playlists.SelectMany(p => p.Tracks).Count();
                // the first two playlists are special playlists needed by Apple Remote
                // this was gleaned by reverse engineering the DACP calls to Itunes
                PlaylistNode node = new PlaylistNode();
                node.Miid = PLAYLIST_LIBRARY_ID;
                node.Mper = (ulong)node.Miid;
                node.Minm = Environment.MachineName + " Library";
                node.Abpl = PlaylistsResponse.TRUE; //important to mark this as the library
                node.Mimc = totalSongCount;
                playlistResponse.Mlcl.AddLast(node);
                node = new PlaylistNode();
                node.Miid = PLAYLIST_MUSIC_ID;
                node.Mper = (ulong)node.Miid;
                node.Minm = "Music";
                node.Aesp = PlaylistsResponse.TRUE; // important to mark this as special playlist
                node.Aeps = PlaylistNode.PLAYLIST_TYPE_MUSIC;  // special playlist for music
                node.Mimc = totalSongCount;
                playlistResponse.Mlcl.AddLast(node);
                foreach (Playlist pl in cache.Value.playlists)
                {
                    string playlistName = pl.Name;
                    int trackCount = pl.Tracks.Count();

                    // skip this loop if no playlist name of if "Accessible Tracks" since that list is TOO BIG, or Imported M3U playlists
                    if ((playlistName == null) ||
                        (playlistName.Length == 0) ||
                        (playlistName.Contains("Accessible Tracks")) ||
                        (playlistName.Contains("Imported")))
                    {
                        continue;
                    }

                    // now create and fill out the playlist node
                    node = new PlaylistNode();
                    node.Miid = pl.Id;
                    node.Mper = (ulong)node.Miid;
                    node.Minm = playlistName;

                    // TODO
                    // if AutoPlaylist = 1 mark it as an Apple Special playlist
                    /*if (Convert.ToInt32(iterator.get_StringByIndex(1)) > 0)
                    {
                        // set Special Playlist and set record count to 1 else it does not show up in UI
                        node.Aesp = PlaylistsResponse.TRUE; // important to mark this as special playlist
                        node.Mimc = 1;
                    }
                    else*/
                    {
                        // use the record count from the database for this regular playlist
                        node.Mimc = trackCount;
                        // set the edit status to 103 for editable
                        node.Meds = PlaylistNode.PLAYLIST_EDIT_STATUS_OTHER;
                    }

                    playlistResponse.Mlcl.AddLast(node);
                }
            }
            catch (Exception ex)
            {
                LOG.Error("Error Loading Playlists...", ex);
            }

            return playlistResponse;

        }

        protected override DACPResponse GetPlaylistTracks(System.Net.HttpListenerRequest request)
        {
            LOG.Info("Getting Playlist Tracks...");
            TracksResponse tracksResponse = new TracksResponse(request);
            try
            {
                switch (tracksResponse.PlaylistId)
                {
                    case PLAYLIST_DJ_ID:
                        tracksResponse = (TracksResponse)GetNowPlaying(request);
                        break;
                    case PLAYLIST_MUSIC_ID:
                        tracksResponse = (TracksResponse)GetNowPlaying(request);
                        break;
                    default:
                        Playlist playlist = cache.Value.playlists
                                             .Where(pl => pl.Id == tracksResponse.PlaylistId).FirstOrDefault();
                        if (playlist != null)
                        {
                            int count = playlist.Tracks.Count;

                            // only allow retrieval of 500 items for performance
                            if (count > 500)
                            {
                                count = 500;
                            }
                            for (int i = 0; i <= count - 1; i++)
                            {
                                Track track = playlist.Tracks[i];
                                TrackNode trackNode = convertSongToTrackNode(track);
                                tracksResponse.AddTrackNode(trackNode);
                            }
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                LOG.Error("Error Getting Playlist Tracks...", ex);
            }
            return tracksResponse;

        }

        protected override DACPResponse GetProperty(System.Net.HttpListenerRequest request)
        {
            DACPResponse dacpResponse = null;
            try
            {
                string property = request.QueryString["properties"];
                if (property.StartsWith(DACPResponse.PROPERTY_VOLUME))
                {
                    VolumeResponse volumeResponse = new VolumeResponse(request);
                    volumeResponse.Cmvo = Convert.ToInt32(mbApi.Player_GetVolume()  * 100);
                    LOG.DebugFormat("Get Volume = {0}", volumeResponse.Cmvo);
                    dacpResponse = volumeResponse;
                }
                else
                {
                    PropertyResponse propResponse = new PropertyResponse(request);
                    string trackUrl = mbApi.NowPlaying_GetFileUrl();
                    Track track = null;
                    if (!String.IsNullOrEmpty(trackUrl) &&
                            cache.Value.tracksByUrl.TryGetValue(trackUrl, out track))
                    {
                        if (mbApi.Player_GetPlayState() == PlayState.Playing)
                        {
                            propResponse.Caps = PlayerStatusUpdateResponse.PLAYING;
                            LOG.Debug("PlayerStatusUpdateResponse PLAYING");
                        }
                        else if (mbApi.Player_GetPlayState() == PlayState.Paused)
                        {
                            propResponse.Caps = PlayerStatusUpdateResponse.PAUSED;
                            LOG.Debug("PlayerStatusUpdateResponse PAUSED");
                        }

                        // fill out the rest of the track info
                        propResponse.Cmvo = Convert.ToInt32(mbApi.Player_GetVolume() * 100);
                        propResponse.Cavs = DACPResponse.FALSE;
                        propResponse.Cave = DACPResponse.FALSE;
                        propResponse.Cash = (byte)(mbApi.Player_GetShuffle() ? 1 : 0);
                        if (mbApi.Player_GetRepeat() == RepeatMode.None)
                            propResponse.Carp = (byte)0;
                        else if (mbApi.Player_GetRepeat() == RepeatMode.All)
                            propResponse.Carp = (byte)2;
                        else if (mbApi.Player_GetRepeat() == RepeatMode.One)
                            propResponse.Carp = (byte)1;
                        
                        propResponse.Cann = track.Title;
                        propResponse.Cana = track.album.artist.Name;
                        propResponse.Canl = track.album.Title;
                        propResponse.Cang = track.Genre;
                        propResponse.Asai = (ulong)track.album.Id;
                        propResponse.Cant = mbApi.NowPlaying_GetDuration() - mbApi.Player_GetPosition();
                        propResponse.Cast = mbApi.NowPlaying_GetDuration();
                        propResponse.CurrentDatabase = DATABASE_ID;
                        propResponse.CurrentPlaylist = PLAYLIST_LIBRARY_ID;
                        propResponse.CurrentPlaylistTrack = mbApi.NowPlayingList_GetCurrentIndex();
                        propResponse.CurrentTrack = track.Id;
                        // media kind default to MUSIC
                        propResponse.Cmmk = DACPResponse.MEDIAKIND_MUSIC;
                        /*
                        if (MediaMonkey.VersionHi >= 4)
                        {
                            switch (track.TrackType)
                            {
                                case 0:
                                case 3:
                                    propResponse.Cmmk = DACPResponse.MEDIAKIND_MUSIC;
                                    break;
                                case 1:
                                    propResponse.Cmmk = DACPResponse.MEDIAKIND_PODCAST;
                                    break;
                                case 2:
                                    propResponse.Cmmk = DACPResponse.MEDIAKIND_AUDIOBOOK;
                                    break;
                                case 4:
                                case 5:
                                case 6:
                                case 7:
                                    propResponse.Cmmk = DACPResponse.MEDIAKIND_VIDEO;
                                    break;
                                default:
                                    propResponse.Cmmk = DACPResponse.MEDIAKIND_MUSIC;
                                    break;
                            }
                        }*/
                    }
                    dacpResponse = propResponse;
                }
            }
            catch (Exception ex)
            {
                LOG.Error("Error Getting Player Status...", ex);
            }
            return dacpResponse;
        }

        private TrackNode convertSongToTrackNode(Track track)
        {
            TrackNode node = new TrackNode();
            node.Asai = (ulong)track.album.Id;
            node.Asal = track.album.Title;
            node.Asar = track.album.artist.Name;
            node.Asdn = (track.DiscNumber <= 0) ? (byte)1 : Convert.ToByte(track.DiscNumber);
            node.Asgn = track.Genre;
            node.Asri = (ulong)track.album.artist.Id;
            node.Astm = track.SongLength;
            ushort trackNo = 0;
            UInt16.TryParse(track.No, out trackNo);
            node.Astn = trackNo;
            // Rating must be returned in base-100 (is base-5 in MusicBee)
            node.Asur = (track.Rating < 0) ? (byte)0 : Convert.ToByte(track.Rating * 20);
            node.Asyr = track.Year;
            node.Miid = track.Id;
            node.Minm = track.Title;
            return node;
        }



        protected override DACPResponse GetTracks(System.Net.HttpListenerRequest request)
        {
            LOG.Info("Gettings Tracks...");
            TracksResponse tracksResponse = new TracksResponse(request);
            try
            {
                string[] mediaKind = tracksResponse.QueryParams.GetValues(DACPResponse.PROPERTY_MEDIAKIND);
                LOG.DebugFormat("MediaKind = {0}", mediaKind);
                if ((mediaKind != null) && !(mediaKind.Contains(ITUNES_MEDIAKIND_MUSIC)))
                {
                    LOG.Warn("iTunes search for content MusicBee does not contain!");
                }
                else
                {
                    List<Track> tracks = FindTracks(tracksResponse);
                    foreach (Track track in tracks)
                    {
                        tracksResponse.AddTrackNode(convertSongToTrackNode(track));
                    }
                }
                if (tracksResponse.Mlcl.Count == 0)
                {
                    LOG.WarnFormat("No Tracks were found for query: {0}", tracksResponse.GetQuery());
                }
            }
            catch (Exception ex)
            {
                LOG.Error("Error Getting Tracks...", ex);
            }

            return tracksResponse;
        }

        private List<Track> FindTracks(TracksResponse tracksResponse)
        {
            StringBuilder builder = new StringBuilder();
            bool isFullLibrarySelectAllowed = tracksResponse.HttpRequest.UserAgent.StartsWith("TunesRemoteSE");

            List<Track> tracks = new List<Track>();

            // don't know why I have to do this...
            List<Tuple<string, string[]>> tuples = new List<Tuple<string,string[]>>();
            foreach (string column in tracksResponse.QueryParams.AllKeys)
            {
                string[] vals = tracksResponse.QueryParams.GetValues(column);
                bool useLike = IsLikeParameter(vals);

                switch (column)
                {
                    case DACPResponse.PROPERTY_ALBUMID:
                        String albumIds = tracksResponse.QueryParams[column];
                        builder.Remove(0, builder.Length);
                        IEnumerable<Album> albums = cache.Value.artistMap.Values.SelectMany(a => a.Albums);
                        if (albumIds.Contains(","))
                        {
                            int[] albumsIdSplit = albumIds.Split(',').Select<string, int>(s => Int32.Parse(s)).ToArray();
                            albums = albums.Where(alb => albumsIdSplit.Contains(alb.Id)).ToList();
                        }
                        else
                        {
                            int albumId = Int32.Parse(albumIds);
                            albums = albums.Where(alb => alb.Id == albumId).ToList();
                        }

                        tracks = albums.SelectMany(alb => alb.Tracks).ToList();

                        // hate using GOTO but in C# is only way to break out of nested loop
                        goto FINISHED;
                    case DACPResponse.PROPERTY_GENRE:
                        if (tracks == null)
                            tracks = cache.Value.tracksByUrl.Values.ToList();

                        if (useLike)
                            tracks = tracks.Where(t => t.Genre.IndexOf(vals[0], StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                        else
                            tracks = tracks.Where(t => vals.Any(v => String.Equals(t.Genre, v, StringComparison.OrdinalIgnoreCase))).ToList();
                        break;
                    case DACPResponse.PROPERTY_ARTISTNAME:
                        if (vals != null)
                        {
                            IEnumerable<Track> tracksArtist = null;
                            if (useLike)
                            {
                                tracksArtist = cache.Value.artistMap.Values
                                        .Where(art => art.Name.IndexOf(vals[0], StringComparison.OrdinalIgnoreCase) >= 0)
                                        .SelectMany(art => art.Albums)
                                        .SelectMany(alb => alb.Tracks);
                            }
                            else
                            {
                                List<Track> tracksArtistList = new List<Track>();
                                foreach (string artistName in vals)
                                {
                                    Artist artist;
                                    if (cache.Value.artistMap.TryGetValue(artistName, out artist))
                                    {
                                        tracksArtistList.AddRange(artist.Albums
                                                                    .SelectMany(al => al.Tracks));
                                    }
                                }
                                tracksArtist = tracksArtistList;
                            }

                            tracks = tracksArtist.Union(tracks).ToList();
                        }
                        break;
                    case DACPResponse.PROPERTY_COMPOSER:
                        if (vals != null)
                        {
                            IEnumerable<Track> tracksComposer = null;
                            if (useLike)
                                tracksComposer = cache.Value.tracksByUrl.Values.Where(t => t.Composer.IndexOf(vals[0], StringComparison.OrdinalIgnoreCase) >= 0);
                            else
                                tracksComposer = cache.Value.tracksByUrl.Values.Where(t => vals.Any(v => String.Equals(t.Composer, v, StringComparison.OrdinalIgnoreCase)));

                            tracks = tracksComposer.Union(tracks).ToList();
                        }
                        break;
                    case DACPResponse.PROPERTY_ALBUMNAME:
                        if (vals != null)
                        {
                            IEnumerable<Track> tracksAlbum = null;
                            if (useLike)
                            {
                                tracksAlbum = cache.Value.artistMap.Values
                                        .SelectMany(art => art.Albums)
                                        .Where(alb => alb.Title.IndexOf(vals[0], StringComparison.OrdinalIgnoreCase) >= 0)
                                        .SelectMany(alb => alb.Tracks);
                            }
                            else
                            {
                                tracksAlbum = cache.Value.artistMap.Values
                                        .SelectMany(art => art.Albums)
                                        .Where(alb => vals.Any(v => String.Equals(alb.Title, v, StringComparison.OrdinalIgnoreCase)))
                                        .SelectMany(alb => alb.Tracks);
                            }

                            tracks = tracksAlbum.Union(tracks).ToList();
                        }
                        break;
                    case DACPResponse.PROPERTY_ITEMNAME:
                        if (vals != null)
                        {
                            IEnumerable<Track> tracksTrack = null;
                            if (useLike)
                            {
                                tracksTrack = cache.Value.tracksById.Values
                                                    .Where(t => t.Title.IndexOf(vals[0], StringComparison.OrdinalIgnoreCase) >= 0);
                            }
                            else
                            {
                                tracksTrack = cache.Value.tracksById.Values
                                                    .Where(t => vals.Any(v => String.Equals(t.Title, v, StringComparison.OrdinalIgnoreCase)));
                            }

                            tracks = tracksTrack.Union(tracks).ToList();
                        }
                        break;
                    case DACPResponse.PROPERTY_ITEMID:
                        List<Track> tracksById = new List<Track>();
                        foreach (string idS in vals)
                        {
                            int id = Int32.Parse(idS);
                            Track track;
                            if (cache.Value.tracksById.TryGetValue(id, out track))
                                tracksById.Add(track);

                        }

                        tracks = tracksById.Union(tracks).ToList();
                        break;
                    default:
                        // unknown so just continue
                        continue;
                }

            }

        FINISHED:
            LOG.Debug("Finished building Track SELECT");

            if (builder.Length > 4)
            {
                // remove the last OR clause
                builder.Remove(builder.Length - 4, 4);
            }
            else
            {
                // TODO???
                /*
                // no filter so just return no query
                if (isFullLibrarySelectAllowed == true)
                {
                    builder.Append(" 1=1 ");
                }
                else
                {
                    LOG.Warn("No Track SQL created because client is asking for the entire library!");
                    return new List<Track>();
                }*/
            }

            // build the sort order
            // TODO
            /*string sortOrder = tracksResponse.GetQuerySort();
            switch (sortOrder)
            {
                case DACPResponse.SORT_ARTIST:
                    builder.Append("ORDER BY Songs.Album, Songs.DiscNumber COLLATE NUMERICSTRING, Songs.TrackNumber COLLATE NUMERICSTRING ");
                    break;
                case DACPResponse.SORT_ALBUM:
                    builder.Append("ORDER BY Songs.Album, Songs.DiscNumber COLLATE NUMERICSTRING, Songs.TrackNumber COLLATE NUMERICSTRING");
                    break;
                case DACPResponse.SORT_TRACK:
                    builder.Append("ORDER BY Songs.Artist ");
                    break;
                default:
                    if (isFullLibrarySelectAllowed == true)
                    {
                        builder.Append("ORDER BY Songs.Artist, Songs.Album, Songs.DiscNumber COLLATE NUMERICSTRING, Songs.TrackNumber COLLATE NUMERICSTRING");
                    }
                    else
                    {
                        builder.Append("ORDER BY Songs.DiscNumber COLLATE NUMERICSTRING, Songs.TrackNumber COLLATE NUMERICSTRING ");
                    }
                    break;
            }
            */

            // add the limit if a limit was sent in
            if (tracksResponse.IsIndexed)
                tracks = tracks.Skip(tracksResponse.StartIndex).Take(tracksResponse.EndIndex - tracksResponse.StartIndex).ToList();

            return tracks;
        }


        protected override DACPResponse GetUpdate(System.Net.HttpListenerRequest request)
        {
            return new UpdateResponse(request);
        }

        protected override DACPResponse GetSpeakers(System.Net.HttpListenerRequest request)
        {
            SpeakerResponse speakerResponse = new SpeakerResponse(request);
            speakerResponse.AddSpeakers("Computer", 0, Convert.ToInt32(mbApi.Player_GetVolume() * 100), DACPResponse.TRUE);
            return speakerResponse;
        }

        protected override DACPResponse SetProperty(System.Net.HttpListenerRequest request)
        {
            string url = request.RawUrl;
            DACPResponse propResponse = new PropertyResponse(request);
            try
            {
                if (url.Contains(DACPResponse.PROPERTY_VOLUME))
                {
                    VolumeResponse volumeResponse = new VolumeResponse(request);
                    double volume = Convert.ToDouble(volumeResponse.Cmvo * 0.01);
                    LOG.DebugFormat("Set Volume = {0}", volume);
                    mbApi.Player_SetVolume((float)volume);
                }
                else if (url.Contains(DACPResponse.PROPERTY_PLAYINGTIME))
                {
                    string propValue = request.QueryString[DACPResponse.PROPERTY_PLAYINGTIME];
                    int playbacktime = Convert.ToInt32(propValue);
                    LOG.InfoFormat("Setting Playback Time = {0}", playbacktime);
                    mbApi.Player_SetPosition(playbacktime);
                }
                else if (url.Contains(DACPResponse.PROPERTY_SHUFFLESTATE))
                {
                    ControlShuffle(request);
                }
                else if (url.Contains(DACPResponse.PROPERTY_REPEATSTATE))
                {
                    ControlRepeat(request);
                }
                else if (url.Contains(DACPResponse.PROPERTY_VOTE))
                {
                    int trackid = DACPResponse.ConvertHexParameterToInt(request, DACPResponse.PROPERTY_ITEM_SPEC);
                    LOG.InfoFormat("Control Vote TrackId: {0}", trackid);
                    Track track;
                    if (cache.Value.tracksById.TryGetValue(trackid, out track))
                    {
                        // TODO???
                        //MediaMonkey.Player.PlaylistAddTrack(track);
                    }
                    SessionBoundResponse.IncrementDatabaseRevision();
                }
                else if (url.Contains(DACPResponse.PROPERTY_VISUALIZER))
                {
                    LOG.Warn("Control Visualizer not implemented MonkeyTunes!");
                }
                else if (url.Contains(DACPResponse.PROPERTY_FULLSCREEN))
                {
                    LOG.Warn("Control FullScreen not implemented MonkeyTunes!");
                }
                else if (url.Contains(DACPResponse.PROPERTY_RATING))
                {
                    LOG.Debug("Setting User Rating On Song");
                    int trackId = DACPResponse.ConvertHexParameterToInt(request, DACPResponse.PROPERTY_ITEM_SPEC);
                    if (trackId == 0)
                    {
                        string songSpec = request.QueryString["song-spec"];
                        if (songSpec != null)
                        {
                            Match m = Regex.Match(songSpec, @"'dmap\.itemid:(\d*)'");
                            if (m.Success && m.Groups.Count > 1)
                            {
                                trackId = Convert.ToInt16(m.Groups[1].Value);
                            }
                        }
                    }
                    int rating = Convert.ToInt32(request.QueryString[DACPResponse.PROPERTY_RATING]);

                    if (rating < 0)
                        rating = 0;

                    LOG.DebugFormat("Updating Track Id = {0} To Rating = '{1}'", trackId, rating);
                    Track track;
                    if (cache.Value.tracksById.TryGetValue(trackId, out track))
                    {
                        // MusicBee has base-5 rating; iTunes base-100:
                        double mbRating = Math.Round(((double)(5 * rating)) / 100);
                        if (mbApi.Library_SetFileTag(track.Url, MetaDataType.Rating, mbRating.ToString()))
                            mbApi.Library_CommitTagsToFile(track.Url);
                    }
                }
                else
                {
                    LOG.WarnFormat("Property value not handled by MonkeyTunes for URL: {0}", url);
                }
            }
            catch (Exception ex)
            {
                LOG.Error("Error Setting Property: " + url, ex);
            }
            LOG.Debug("Set Property Finished...");
            propResponse = null;
            return propResponse;
        }

        protected override void ControlClearQueue(System.Net.HttpListenerRequest request)
        {
            mbApi.NowPlayingList_Clear();
        }

        protected override void ControlNextItem(System.Net.HttpListenerRequest request)
        {
            mbApi.Player_PlayNextTrack();
        }

        protected override void ControlPause(System.Net.HttpListenerRequest request)
        {
            if (mbApi.Player_GetPlayState() == PlayState.Playing)
                mbApi.Player_PlayPause();
        }

        protected override void ControlPlayPause(System.Net.HttpListenerRequest request)
        {
            mbApi.Player_PlayPause();
        }

        protected override void ControlPreviousItem(System.Net.HttpListenerRequest request)
        {
            mbApi.Player_PlayPreviousTrack();
        }

        protected override void ControlRepeat(System.Net.HttpListenerRequest request)
        {
			LOG.Debug("Repeating...");
			try {
				string propValue = request.QueryString[DACPResponse.PROPERTY_REPEATSTATE];
				int repeatFlag = Convert.ToInt32(propValue);
                LOG.InfoFormat("Setting Repeat State = {0}", repeatFlag);
                switch (repeatFlag)
                {
					case 1:
                        mbApi.Player_SetRepeat(RepeatMode.One);
						break;
					case 2:
                        mbApi.Player_SetRepeat(RepeatMode.All);
						break;
					default:
                        mbApi.Player_SetRepeat(RepeatMode.None);
						break;
				}
			} catch (Exception ex) {
				LOG.Error("Error Setting Repeat State:" + ex.Message, ex);
			}
        }

        protected override void ControlShuffle(System.Net.HttpListenerRequest request)
        {
            LOG.Debug("Shuffling...");
            try
            {
                string propValue = request.QueryString[DACPResponse.PROPERTY_SHUFFLESTATE];
                bool shuffle = false;
                if (propValue != null)
                {
                    shuffle = Convert.ToBoolean(Convert.ToInt32(propValue));
                }
                else
                {
                    shuffle = false;
                }
                LOG.InfoFormat("Setting Shuffle State = {0}", shuffle);
                mbApi.Player_SetShuffle(shuffle);
            }
            catch (Exception ex)
            {
                LOG.Error("Error Setting Shuffle State:" + ex.Message, ex);
            }
        }

        protected override void ControlStop(System.Net.HttpListenerRequest request)
        {
            mbApi.Player_Stop();
        }

        protected override void ControlFastForward(System.Net.HttpListenerRequest request)
        {
            LOG.Debug("Control Fast Forward");
            isRewinding = false;
            timer.AutoReset = true;
            timer.Start();
        }

        protected override void ControlRewind(System.Net.HttpListenerRequest request)
        {
            LOG.Debug("Control Rewind");
            isRewinding = false;
            timer.AutoReset = true;
            timer.Start();
        }

        protected override void ControlPlayResume(System.Net.HttpListenerRequest request)
        {
            LOG.Debug("Control Play Resume");
            timer.Stop();
        }

        protected override void ControlGeniusSeed(System.Net.HttpListenerRequest request)
        {
        }

        protected override void QueueTracks(System.Net.HttpListenerRequest request, bool clearQueue, bool beginPlaying)
        {
            LOG.Info("Queuing Tracks...");

            processMBEvents = false;
            try
            {
                TracksResponse tracksResponse = new TracksResponse(request);
                List<Track> tracks = FindTracks(tracksResponse);
                if (tracks.Count > 0)
                {
                    if (clearQueue)
                    {
                        LOG.Info("Clearing current playlist cue");
                        mbApi.NowPlayingList_Clear();
                        ClearQueue = false;
                    }

                    mbApi.NowPlayingList_QueueFilesLast(tracks.Select(tr => tr.Url).ToArray());
                }
                else
                {
                    tracksResponse = (TracksResponse)GetNowPlaying(request);
                }

                if ((PairingDatabase.RespectClearCueCommand) && (beginPlaying))
                {
                    string url = mbApi.NowPlayingList_GetListFileUrl(tracksResponse.GetQueryIndex());
                    if (!String.IsNullOrEmpty(url))
                        mbApi.NowPlayingList_PlayNow(url);
                    /*int currentIndex = mbApi.NowPlayingList_GetCurrentIndex();

                    if (tracksResponse.GetQueryIndex() > currentIndex)
                    {
                        while (mbApi.NowPlayingList_GetCurrentIndex() != tracksResponse.GetQueryIndex()
                                && mbApi.NowPlayingList_IsAnyFollowingTracks())
                            mbApi.Player_PlayNextTrack();
                    }
                    if (tracksResponse.GetQueryIndex() < currentIndex)
                    {
                        while (mbApi.NowPlayingList_GetCurrentIndex() != tracksResponse.GetQueryIndex()
                                && mbApi.NowPlayingList_IsAnyPriorTracks())
                            mbApi.Player_PlayPreviousTrack();
                    }
                    else
                    {
                        if (mbApi.Player_GetPlayState() != PlayState.Playing)
                            mbApi.Player_PlayPause();
                    }
                    */
                    ControlShuffle(request);
                }

            }
            catch (Exception ex)
            {
                LOG.Error("Error Queuing Tracks...", ex);
            }
            finally
            {
                processMBEvents = true;
            }

            ReleaseAllLatches();
        }

        protected override void SetPlaylist(System.Net.HttpListenerRequest request)
        {
            LOG.Info("Set Playlist...");
            try
            {
                int playlistId = DACPResponse.ConvertHexParameterToInt(request, DACPResponse.PROPERTY_CONTAINER_SPEC);
                int playlistItemId = DACPResponse.ConvertHexParameterToInt(request, DACPResponse.PROPERTY_CONTAINER_ITEM_SPEC);

                switch (playlistId)
                {
                    case PLAYLIST_DJ_ID:
                        // do nothing
                        return;
                    case PLAYLIST_MUSIC_ID:
                        // just set the track and play it
                        break;
                    default:
                        LOG.InfoFormat("Adding Playlist to Queue PlaylistID = {0} ItemId = {1}", playlistId, playlistItemId);
                        Playlist playlist = cache.Value.playlists.Where(pl => pl.Id == playlistId).FirstOrDefault();
                        if (playlist != null)
                        {
                            mbApi.Playlist_PlayNow(playlist.Url);
                            string url = mbApi.NowPlayingList_GetListFileUrl(playlistItemId);
                            if (!String.IsNullOrEmpty(url))
                                mbApi.NowPlayingList_PlayNow(url);
                        }
                        break;
                }

                /* TODO
                if (playlistItemId > 0)
                {
                    MediaMonkey.Player.CurrentSongIndex = playlistItemId;
                }
                else
                {
                    MediaMonkey.Player.CurrentSongIndex = 0;
                }
                */
                if (mbApi.Player_GetPlayState() != PlayState.Playing)
                {
                    mbApi.Player_PlayPause();
                }
                ReleaseAllLatches();
            }
            catch (Exception ex)
            {
                LOG.Error("Error Setting Playlist Tracks...", ex);
            }
        }

        protected override void SetSpeakers(System.Net.HttpListenerRequest request)
        {
            return;
        }

        protected override void Logout(System.Net.HttpListenerRequest request)
        {
            cache = new Lazy<MusicBeeLibCache>(() =>
            {
                return new MusicBeeLibCache(mbApi);
            });
        }

        public override void RefreshCache()
        {
            cache = new Lazy<MusicBeeLibCache>(() =>
            {
                return new MusicBeeLibCache(mbApi);
            });

            // init the cache:
            MusicBeeLibCache tmp = cache.Value;
        }
    }
}
