using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Collections;
using log4net;

namespace DacpMusicBeePlugin
{
    public class Artist
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public List<Album> Albums { get; set; }
    }

    public class Album
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public List<Track> Tracks { get; set; }
        public Artist artist { get; set; }
    }

    public class Track : IEqualityComparer<Track>
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public string No { get; set; }
        public string Url { get; set; }
        public int Year { get; set; }
        public float Rating { get; set; }
        public int SongLength { get; set; }
        public string Genre { get; set; }
        public string Composer { get; set; }
        public int DiscNumber { get; set; }
        public Album album { get; set; }

        #region IEqualityComparer<Track> Members

        public bool Equals(Track x, Track y)
        {
            return x.Id == y.Id;
        }

        public int GetHashCode(Track obj)
        {
            return obj.Id;
        }

        #endregion
    }

    public class Playlist
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Url { get; set; }
        public List<Track> Tracks { get; set; }
    }


    public class MusicBeeLibCache
    {
        private static readonly ILog LOG = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);


        public Dictionary<string, Artist> artistMap = new Dictionary<string,Artist>();
        public Dictionary<int, Track> tracksById = new Dictionary<int, Track>();
        public Dictionary<string, Track> tracksByUrl = new Dictionary<string,Track>();

        public List<Playlist> playlists = new List<Playlist>();

        public MusicBeeLibCache(MusicBeePlugin.Plugin.MusicBeeApiInterface mbApi)
        {
            artistMap = new Dictionary<string,Artist>();

            if (mbApi.Library_QueryFiles(""))
            {
                int artistId = 1;
                int albumId = 1;
                int trackId = 1;
                while (true)
                {
                    string currentFile = mbApi.Library_QueryGetNextFile();
                    if (String.IsNullOrEmpty(currentFile))
                        break;

                    string artistName = mbApi.Library_GetFileTag(currentFile, MusicBeePlugin.Plugin.MetaDataType.AlbumArtist);
                    string albumTitle = mbApi.Library_GetFileTag(currentFile, MusicBeePlugin.Plugin.MetaDataType.Album);
                    string trackTitle = mbApi.Library_GetFileTag(currentFile, MusicBeePlugin.Plugin.MetaDataType.TrackTitle);
                    if (string.IsNullOrEmpty(artistName) || string.IsNullOrEmpty(albumTitle) || string.IsNullOrEmpty(trackTitle))
                        continue;

                    string composer = mbApi.Library_GetFileTag(currentFile, MusicBeePlugin.Plugin.MetaDataType.Composer);
                    string genre = mbApi.Library_GetFileTag(currentFile, MusicBeePlugin.Plugin.MetaDataType.Genre);
                    string trackN = mbApi.Library_GetFileTag(currentFile, MusicBeePlugin.Plugin.MetaDataType.TrackNo);
                    string duration = mbApi.Library_GetFileProperty(currentFile, MusicBeePlugin.Plugin.FilePropertyType.Duration);
                    string rating = mbApi.Library_GetFileTag(currentFile, MusicBeePlugin.Plugin.MetaDataType.Rating);

                    Artist artist = null;
                    if (!artistMap.TryGetValue(artistName, out artist))
                    {
                        artist = new Artist();
                        artist.Albums = new List<Album>();
                        artist.Id = artistId++;
                        artist.Name = artistName;
                        artistMap[artistName] = artist;
                    }

                    Album album = artist.Albums.FirstOrDefault(a => a.Title == albumTitle);
                    if (album == null)
                    {
                        album = new Album();
                        album.Tracks = new List<Track>();
                        album.Id = albumId++;
                        album.Title = albumTitle;
                        album.artist = artist;
                        artist.Albums.Add(album);
                    }

                    Track track = new Track();
                    track.Id = trackId++;
                    track.Url = currentFile;
                    track.Title = trackTitle;
                    track.No = trackN;
                    track.album = album;
                    track.Genre = genre;
                    if (string.IsNullOrEmpty(track.Genre))
                        track.Genre = "[EMPTY]";
                    track.Composer = composer;
                    if (string.IsNullOrEmpty(track.Composer))
                        track.Composer = "[EMPTY]";
                    string[] durationArray = duration.Split(':');
                    int durationInt = 0;
                    foreach (String str in durationArray)
                    {
                        durationInt *= 60;
                        int parsedInt = 0;
                        if (int.TryParse(str, out parsedInt))
                            durationInt += parsedInt;
                        else
                            LOG.Debug("Can not parse duration: " + duration + "(" + str + ")");
                    }
                    track.SongLength = durationInt * 1000;
                    if (!String.IsNullOrEmpty(rating))
                        track.Rating = float.Parse(rating);
                    album.Tracks.Add(track);
                }

                IEnumerable<Track> tracks = artistMap.Values.SelectMany(art => art.Albums).SelectMany(alb => alb.Tracks);

                tracksById = tracks.ToDictionary(tr => tr.Id);
                tracksByUrl = tracks.ToDictionary(tr => tr.Url);
            }

            if (mbApi.Playlist_QueryPlaylists())
            {
                int i = 1;
                while (true)
                {
                    string currentFile = mbApi.Playlist_QueryGetNextPlaylist();
                    if (String.IsNullOrEmpty(currentFile))
                        break;

                    Playlist pl = new Playlist();
                    pl.Id = i++;
                    pl.Url = currentFile;
                    pl.Name = mbApi.Playlist_GetName(currentFile);
                    pl.Tracks = new List<Track>();
                    if (mbApi.Playlist_QueryFiles(currentFile))
                    {
                        while (true)
                        {
                            string trackUrl = mbApi.Playlist_QueryGetNextFile();
                            if (String.IsNullOrEmpty(trackUrl))
                                break;
                            Track track;
                            if (tracksByUrl.TryGetValue(trackUrl, out track))
                                pl.Tracks.Add(track);
                        }
                    }
                    playlists.Add(pl);
                }
            }

        }

        public Album FindAlbumById(int id)
        {
            return artistMap.Values.SelectMany(art => art.Albums).First(alb => alb.Id == id);
        }
    }


}
