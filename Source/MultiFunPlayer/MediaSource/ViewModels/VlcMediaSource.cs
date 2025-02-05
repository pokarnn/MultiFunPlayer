using MultiFunPlayer.Common;
using MultiFunPlayer.Shortcut;
using MultiFunPlayer.UI;
using Newtonsoft.Json.Linq;
using NLog;
using Stylet;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using System.Xml.XPath;

namespace MultiFunPlayer.MediaSource.ViewModels;

[DisplayName("VLC")]
internal sealed class VlcMediaSource(IShortcutManager shortcutManager, IEventAggregator eventAggregator) : AbstractMediaSource(shortcutManager, eventAggregator)
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private PlayerState _playerState;

    public override ConnectionStatus Status { get; protected set; }
    public bool IsConnected => Status == ConnectionStatus.Connected;
    public bool IsDisconnected => Status == ConnectionStatus.Disconnected;
    public bool IsConnectBusy => Status == ConnectionStatus.Connecting || Status == ConnectionStatus.Disconnecting;
    public bool CanToggleConnect => !IsConnectBusy && !string.IsNullOrEmpty(Password);

    public EndPoint Endpoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 8080);
    public string Password { get; set; } = null;

    protected override async Task RunAsync(CancellationToken token)
    {
        try
        {
            Logger.Info("Connecting to {0} at \"{1}\"", Name, Endpoint.ToUriString());
            if (Endpoint == null)
                throw new Exception("Endpoint cannot be null.");

            using var client = NetUtils.CreateHttpClient();
            client.Timeout = TimeSpan.FromMilliseconds(1000);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($":{Password}")));

            var uri = new Uri($"http://{Endpoint.ToUriString()}/requests/status.xml");
            var response = await UnwrapTimeout(() => client.GetAsync(uri, token));
            response.EnsureSuccessStatusCode();

            Status = ConnectionStatus.Connected;
            ClearPendingMessages();

            _playerState = new PlayerState();

            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            var task = await Task.WhenAny(ReadAsync(client, cancellationSource.Token), WriteAsync(client, cancellationSource.Token));
            cancellationSource.Cancel();

            task.ThrowIfFaulted();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Logger.Error(e, $"{Name} failed with exception");
            _ = DialogHelper.ShowErrorAsync(e, $"{Name} failed with exception", "RootDialog");
        }

        if (IsDisposing)
            return;

        PublishMessage(new MediaPathChangedMessage(null));
        PublishMessage(new MediaPlayingChangedMessage(false));
    }

    private async Task ReadAsync(HttpClient client, CancellationToken token)
    {
        var statusUri = new Uri($"http://{Endpoint.ToUriString()}/requests/status.xml");
        var playlistUri = new Uri($"http://{Endpoint.ToUriString()}/requests/playlist.xml");

        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(200, token);

                var statusResponse = await UnwrapTimeout(() => client.GetAsync(statusUri, token));
                if (statusResponse == null)
                    continue;

                statusResponse.EnsureSuccessStatusCode();
                var statusMessage = await statusResponse.Content.ReadAsStringAsync(token);

                Logger.Trace("Received \"{0}\" from \"{1}\"", statusMessage, Name);
                var statusDocument = XDocument.Parse(statusMessage);

                if (!int.TryParse(statusDocument.Root.Element("currentplid")?.Value, out var playlistId))
                    continue;

                var lastPlaylistId = _playerState.PlaylistId;
                if (playlistId < 0)
                {
                    ResetState();
                    continue;
                }

                if (playlistId != lastPlaylistId)
                {
                    var playlistResponse = await UnwrapTimeout(() => client.GetAsync(playlistUri, token));
                    if (playlistResponse == null)
                        continue;

                    playlistResponse.EnsureSuccessStatusCode();
                    var playlistMessage = await playlistResponse.Content.ReadAsStringAsync(token);

                    Logger.Trace("Received \"{0}\" from \"{1}\"", playlistMessage, Name);

                    var playlistDocument = XDocument.Parse(playlistMessage);
                    var element = playlistDocument.XPathSelectElement($".//leaf[@id={playlistId}]");
                    var path = element?.Attribute("uri")?.Value;

                    if (string.IsNullOrWhiteSpace(path))
                    {
                        ResetState();
                        continue;
                    }

                    if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile)
                        PublishMessage(new MediaPathChangedMessage(uri.LocalPath));
                    else if (Uri.TryCreate(path, UriKind.RelativeOrAbsolute, out uri))
                        PublishMessage(new MediaPathChangedMessage(uri.ToString()));
                    else
                        PublishMessage(new MediaPathChangedMessage(Uri.UnescapeDataString(path)));

                    _playerState.PlaylistId = playlistId;
                }

                if (statusDocument.Root.Element("state")?.Value is string state && state != _playerState.State)
                {
                    PublishMessage(new MediaPlayingChangedMessage(string.Equals(state, "playing", StringComparison.OrdinalIgnoreCase)));
                    _playerState.State = state;
                }

                if (int.TryParse(statusDocument.Root.Element("length")?.Value, out var duration) && duration >= 0 && duration != _playerState.Duration)
                {
                    PublishMessage(new MediaDurationChangedMessage(TimeSpan.FromSeconds(duration)));
                    _playerState.Duration = duration;
                }

                if (double.TryParse(statusDocument.Root.Element("position")?.Value, out var position) && position >= 0 && position != _playerState.Position && _playerState.Duration != null)
                {
                    PublishMessage(new MediaPositionChangedMessage(TimeSpan.FromSeconds(position * (double)_playerState.Duration)));
                    _playerState.Position = position;
                }

                if (double.TryParse(statusDocument.Root.Element("rate")?.Value, out var speed) && speed > 0 && speed != _playerState.Speed)
                {
                    PublishMessage(new MediaSpeedChangedMessage(speed));
                    _playerState.Speed = speed;
                }
            }
        }
        catch (OperationCanceledException) { }

        void ResetState()
        {
            _playerState = new PlayerState();

            PublishMessage(new MediaPathChangedMessage(null));
            PublishMessage(new MediaPlayingChangedMessage(false));
        }
    }

    private async Task WriteAsync(HttpClient client, CancellationToken token)
    {
        var uriBase = $"http://{Endpoint.ToUriString()}/requests/status.xml";

        try
        {
            while (!token.IsCancellationRequested)
            {
                await WaitForMessageAsync(token);
                var message = await ReadMessageAsync(token);

                var isPlaying = string.Equals(_playerState?.State, "playing", StringComparison.OrdinalIgnoreCase);
                var uriArguments = message switch
                {
                    MediaPlayPauseMessage playPauseMessage when isPlaying != playPauseMessage.ShouldBePlaying => "pl_pause",
                    MediaSeekMessage seekMessage => $"seek&val={(int)seekMessage.Position.TotalSeconds}",
                    MediaChangePathMessage changePathMessage => string.IsNullOrWhiteSpace(changePathMessage.Path) ? "pl_stop" : $"in_play&input={Uri.EscapeDataString(changePathMessage.Path)}",
                    MediaChangeSpeedMessage changeSpeedMessage => $"rate&val={changeSpeedMessage.Speed.ToString("F4").Replace(',', '.')}",
                    _ => null
                };

                if (uriArguments == null)
                    continue;

                var requestUri = new Uri($"{uriBase}?command={uriArguments}");
                Logger.Trace("Sending \"{0}\" to \"{1}\"", uriArguments, Name);

                var response = await UnwrapTimeout(() => client.GetAsync(requestUri, token));
                response.EnsureSuccessStatusCode();
            }
        }
        catch (OperationCanceledException) { }
    }

    public override void HandleSettings(JObject settings, SettingsAction action)
    {
        base.HandleSettings(settings, action);

        if (action == SettingsAction.Saving)
        {
            settings[nameof(Endpoint)] = Endpoint?.ToUriString();

            try
            {
                if (!string.IsNullOrWhiteSpace(Password))
                {
                    var encryptedPassword = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(Password), null, DataProtectionScope.CurrentUser));
                    settings[nameof(Password)] = JToken.FromObject(encryptedPassword);
                }
                else
                {
                    settings[nameof(Password)] = null;
                }
            }
            catch (Exception e)
            {
                Logger.Warn(e, "Failed to encrypt password to settings");
            }
        }
        else if (action == SettingsAction.Loading)
        {
            if (settings.TryGetValue<EndPoint>(nameof(Endpoint), out var endpoint))
                Endpoint = endpoint;

            if (settings.TryGetValue<string>(nameof(Password), out var encryptedPassword))
            {
                try
                {
                    Password = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(encryptedPassword), null, DataProtectionScope.CurrentUser));
                }
                catch (Exception e)
                {
                    Logger.Warn(e, "Failed to decrypt password from settings");
                }
            }
        }
    }

    public override async ValueTask<bool> CanConnectAsync(CancellationToken token)
    {
        try
        {
            if (Endpoint == null)
                return false;

            var uri = new Uri($"http://{Endpoint.ToUriString()}");

            using var client = NetUtils.CreateHttpClient();
            client.Timeout = TimeSpan.FromMilliseconds(50);

            var response = await client.GetAsync(uri, token);
            response.EnsureSuccessStatusCode();

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<HttpResponseMessage> UnwrapTimeout(Func<Task<HttpResponseMessage>> action)
    {
        //https://github.com/dotnet/runtime/issues/21965

        try
        {
            return await action();
        }
        catch (Exception e)
        {
            if (e is OperationCanceledException operationCanceledException)
            {
                var innerException = operationCanceledException.InnerException;
                if (innerException is TimeoutException)
                    innerException.Throw();

                operationCanceledException.Throw();
            }

            e.Throw();
            return null;
        }
    }

    protected override void RegisterActions(IShortcutManager s)
    {
        base.RegisterActions(s);

        #region Endpoint
        s.RegisterAction<string>($"{Name}::Endpoint::Set", s => s.WithLabel("Endpoint").WithDescription("ipOrHost:port"), endpointString =>
        {
            if (NetUtils.TryParseEndpoint(endpointString, out var endpoint))
                Endpoint = endpoint;
        });
        #endregion
    }

    private sealed class PlayerState
    {
        public double? Position { get; set; }
        public double? Speed { get; set; }
        public string State { get; set; }
        public int? Duration { get; set; }
        public int? PlaylistId { get; set; }
    }
}
