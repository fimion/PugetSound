﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PugetSound.Auth;
using PugetSound.Logic;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Enums;
using SpotifyAPI.Web.Models;

namespace PugetSound
{
    public class PartyRoom
    {
        private readonly ILogger _logger;
        private readonly SpotifyAccessService _spotifyAccessService;
        private DateTimeOffset _handledUntil;
        private DateTimeOffset _timeSinceEmpty;
        private int _currentDjNumber;
        private FullTrack _currentTrack;

        private readonly DateTimeOffset CustomFutureDateTimeOffset = new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public DateTimeOffset TimeSinceEmpty => _timeSinceEmpty;

        public string RoomId { get; }

        public RoomState CurrentRoomState { get; private set; }

        private readonly List<RoomMember> _members;

        public IReadOnlyCollection<RoomMember> Members => _members;

        private readonly List<IRoomEvent> _roomEvents;

        public IReadOnlyCollection<IRoomEvent> RoomEvents => _roomEvents;

        public PartyRoom(string roomId, ILogger logger, SpotifyAccessService spotifyAccessService)
        {
            _logger = logger;
            _spotifyAccessService = spotifyAccessService;
            RoomId = roomId;
            _members = new List<RoomMember>();
            _roomEvents = new List<IRoomEvent>();
            _timeSinceEmpty = CustomFutureDateTimeOffset;

            _handledUntil = DateTimeOffset.Now;
            _currentDjNumber = -1;
            _currentTrack = null;
            CurrentRoomState = new RoomState();
        }

        public event EventHandler<string> OnRoomMembersChanged;

        public event EventHandler<RoomNotification> OnRoomNotification;

        public void MemberJoin(RoomMember member)
        {
            if (_members.Any(x => x.UserName == member.UserName)) return;

            _members.Add(member);
            OnRoomMembersChanged?.Invoke(this, member.UserName);

            _roomEvents.Add(new UserEvent(member.UserName, member.FriendlyName, UserEventType.JoinedRoom));
            OnRoomNotification?.Invoke(this, new RoomNotification
            {
                Category = RoomNotificationCategory.Information,
                Message = $"{member.FriendlyName} joined room"
            });

            ToggleDj(member, false);

            if (_currentTrack != null) StartSongForMemberUgly(member);

            _timeSinceEmpty = CustomFutureDateTimeOffset;
        }

        private async void StartSongForMemberUgly(RoomMember member)
        {
            var left = _handledUntil.ToUnixTimeMilliseconds() - DateTimeOffset.Now.ToUnixTimeMilliseconds();
            await PlaySong(member, _currentTrack, (int) (_currentTrack.DurationMs - left));
        }

        public void ToggleDj(RoomMember member, bool isDj)
        {
            member.IsDj = isDj;

            member.DjOrderNumber = isDj ? _members.Where(x => x.IsDj).Max(y => y.DjOrderNumber) + 1 : -1;

            _roomEvents.Add(new UserEvent(member.UserName, member.FriendlyName, isDj ? UserEventType.BecameDj : UserEventType.BecameListener));
            OnRoomNotification?.Invoke(this, new RoomNotification
            {
                Category = RoomNotificationCategory.Information,
                Message = $"{member.FriendlyName} became a {(isDj ? "DJ" : "listener")}"
            });

            OnRoomMembersChanged?.Invoke(this, null);
        }

        public void VoteSkipSong(RoomMember member)
        {
            var oldVal = member.VotedSkipSong;

            member.VotedSkipSong = true;

            if (oldVal == false)
            {
                OnRoomNotification?.Invoke(this, new RoomNotification
                {
                    Category = RoomNotificationCategory.Information,
                    Message = $"{member.FriendlyName} voted to skip song"
                });
            }

            if (_members.Count / 2 > _members.Count(x => x.VotedSkipSong)) return;

            _roomEvents.Add(new SongSkippedEvent());
            OnRoomNotification?.Invoke(this, new RoomNotification
            {
                Category = RoomNotificationCategory.Success,
                Message = $"Skipping song with {_members.Count(x => x.VotedSkipSong)} votes"
            });

            _handledUntil = DateTimeOffset.Now;
            foreach (var roomMember in _members)
            {
                roomMember.VotedSkipSong = false;
            }
        }

        public void MemberLeave(RoomMember member)
        {
            var didRemove = _members.Remove(member);
            if (!didRemove) return;

            _roomEvents.Add(new UserEvent(member.UserName, member.FriendlyName, UserEventType.LeftRoom));
            OnRoomNotification?.Invoke(this, new RoomNotification
            {
                Category = RoomNotificationCategory.Information,
                Message = $"{member.FriendlyName} left the room"
            });

            OnRoomMembersChanged?.Invoke(this, member.UserName);

            // this was the last member to leave
            if (!_members.Any())
            {
                _timeSinceEmpty = DateTimeOffset.Now;
            }
        }

        public async Task<RoomState> TryPlayNext(bool force = false)
        {
            while (true)
            {
                // return if song is playing right now, except when we're skipping a song
                if (!force && DateTimeOffset.Now < _handledUntil)
                {
                    // we don't change room state here
                    return new RoomState();
                }

                _currentTrack = null;

                // try getting next player
                var nextPlayer = GetNextPlayer();

                // if we don't find any we don't have a DJ - this will exit the recursion if we run out of DJs
                if (nextPlayer == null)
                {
                    CurrentRoomState = new RoomState();
                    return CurrentRoomState;
                }

                var song = await GetSongFromQueue(nextPlayer, nextPlayer.PlaylistId);

                // success
                if (song != null)
                {
                    _currentDjNumber = nextPlayer.DjOrderNumber;

                    // do the loop on a tmp list of members, so if someone joins mid-play we don't err out
                    var tmpMembers = _members.ToList();

                    // start songs for everyone (OLD)
                    //foreach (var roomMember in tmpMembers)
                    //{
                    //    await PlaySong(roomMember, song);
                    //}

                    // start songs for everyone (NEW)
                    var sw = new Stopwatch();
                    sw.Start();
                    var playTasks = tmpMembers.Select(x => PlaySong(x, song)).ToList();
                    await Task.WhenAll(playTasks);
                    sw.Stop();
                    _logger.Log(LogLevel.Information, "Took {TimedApiPlayForAll} to start songs for {MemberCount} room members", sw.Elapsed, tmpMembers.Count);

                    // set handled
                    _handledUntil = DateTimeOffset.Now.AddMilliseconds(song.DurationMs);
                    _currentTrack = song;

                    // return state
                    CurrentRoomState = new RoomState
                    {
                        IsPlayingSong = true,
                        CurrentDjUsername = nextPlayer.UserName,
                        CurrentDjName = nextPlayer.FriendlyName,
                        CurrentSongArtist = string.Join(", ", song.Artists.Select(x => x.Name).ToArray()),
                        CurrentSongTitle = song.Name,
                        CurrentSongArtUrl = song?.Album.Images.FirstOrDefault()?.Url ?? "/images/missingart.jpg",
                        SongStartedAtUnixTimestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                        SongFinishesAtUnixTimestamp = _handledUntil.ToUnixTimeMilliseconds()
                    };

                    _roomEvents.Add(new SongPlayedEvent(nextPlayer.UserName, nextPlayer.FriendlyName,
                        $"{CurrentRoomState.CurrentSongArtist} - {CurrentRoomState.CurrentSongTitle}", song.Id, song.Uri));

                    return CurrentRoomState;
                }

                // remove user as DJ if song is null
                nextPlayer.IsDj = false;
                OnRoomMembersChanged?.Invoke(this, null);

                // then try again
            }
        }

        private async Task PlaySong(RoomMember member, FullTrack song, int positionMs = 0)
        {
            try
            {
                var api = await _spotifyAccessService.TryGetMemberApi(member.UserName);

                var devices = await api.GetDevicesAsync();

                devices.ThrowOnError(nameof(api.GetDevices));

                if (!devices.Devices.Any()) throw new Exception("No devices available to play on!");

                var device = devices.Devices.FirstOrDefault(x => x.IsActive) ?? devices.Devices.First();

                var resume = await api.ResumePlaybackAsync(deviceId:device.Id, uris: new List<string> { song.Uri }, offset: 0, positionMs: positionMs);

                resume.ThrowOnError(nameof(api.ResumePlaybackAsync));

                // we don't care if this one fails
                await api.SetRepeatModeAsync(RepeatState.Off, device.Id);
            }
            catch (Exception e)
            {
                _logger.Log(LogLevel.Warning, "Failed to play song for {Username} because {@Exception}", member.UserName, e);
                OnRoomNotification?.Invoke(this, new RoomNotification
                {
                    Category = RoomNotificationCategory.Error,
                    Message = $"Failed to play song",
                    TargetId = member.ConnectionId
                });
                // oh well
                Debug.WriteLine(e);
            }
        }

        private async Task<FullTrack> GetSongFromQueue(RoomMember member, string playlist)
        {
            try
            {
                var api = await _spotifyAccessService.TryGetMemberApi(member.UserName);

                var queueList = await api.GetPlaylistTracksAsync(playlist);

                queueList.ThrowOnError(nameof(api.GetPlaylistTracks));

                if (!queueList.Items.Any()) return null;

                var track = queueList.Items.First().Track;

                var remove = await api.RemovePlaylistTrackAsync(playlist, new DeleteTrackUri(track.Uri, 0));

                remove.ThrowOnError(nameof(api.RemovePlaylistTrackAsync));

                return track;

            }
            catch (Exception e)
            {
                _logger.Log(LogLevel.Warning, "Failed to get song from {Username}'s queue because {@Exception}", member.UserName, e);
                OnRoomNotification?.Invoke(this, new RoomNotification
                {
                    Category = RoomNotificationCategory.Warning,
                    Message = $"Failed to get song from {member.FriendlyName}'s queue"
                });
                Debug.WriteLine(e);
                return null;
            }
        }

        private RoomMember GetNextPlayer()
        {
            if (!_members.Any(x => x.IsDj)) return null;
            var orderedDjs = _members.Where(x => x.IsDj).OrderBy(y => y.DjOrderNumber).ToList();
            var nextDj = orderedDjs.FirstOrDefault(x => x.DjOrderNumber > _currentDjNumber);
            return nextDj ?? orderedDjs.First();
        }

        public async Task AddToLiked(RoomMember member)
        {
            try
            {
                var api = await _spotifyAccessService.TryGetMemberApi(member.UserName);

                var track = _currentTrack;

                var result = await api.SaveTrackAsync(track.Id);

                result.ThrowOnError(nameof(api.SaveTrackAsync));

                OnRoomNotification?.Invoke(this, new RoomNotification
                {
                    Category = RoomNotificationCategory.Success,
                    Message = $"Successfully added {string.Join(", ", track.Artists.Select(x => x.Name).ToArray())} - {track.Name} to your Liked Songs",
                    TargetId = member.ConnectionId
                });
            }
            catch (Exception e)
            {
                _logger.Log(LogLevel.Warning, "Failed to add song to {Username}'s liked songs because {@Exception}", member.UserName, e);
                OnRoomNotification?.Invoke(this, new RoomNotification
                {
                    Category = RoomNotificationCategory.Error,
                    Message = $"Failed to add song to your Liked Songs",
                    TargetId = member.ConnectionId
                });
                Debug.WriteLine(e);
            }
        }
    }
}
