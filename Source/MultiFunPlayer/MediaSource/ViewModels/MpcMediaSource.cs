using MultiFunPlayer.Common;
using MultiFunPlayer.Shortcut;
using MultiFunPlayer.UI;
using Newtonsoft.Json.Linq;
using NLog;
using Stylet;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace MultiFunPlayer.MediaSource.ViewModels;

[DisplayName("MPC-HC")]
internal sealed class MpcMediaSource(IShortcutManager shortcutManager, IEventAggregator eventAggregator) : AbstractMediaSource(shortcutManager, eventAggregator)
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public override ConnectionStatus Status { get; protected set; }
    public bool IsConnected => Status == ConnectionStatus.Connected;
    public bool IsDisconnected => Status == ConnectionStatus.Disconnected;
    public bool IsConnectBusy => Status == ConnectionStatus.Connecting || Status == ConnectionStatus.Disconnecting;
    public bool CanToggleConnect => !IsConnectBusy;

    public EndPoint Endpoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 13579);

    protected override async Task RunAsync(CancellationToken token)
    {
        try
        {
            Logger.Info("Connecting to {0} at \"{1}\"", Name, Endpoint.ToUriString());
            if (Endpoint == null)
                throw new Exception("Endpoint cannot be null.");

            using var client = NetUtils.CreateHttpClient();
            client.Timeout = TimeSpan.FromMilliseconds(1000);

            var uri = new Uri($"http://{Endpoint.ToUriString()}");
            var response = await UnwrapTimeout(() => client.GetAsync(uri, token));
            response.EnsureSuccessStatusCode();

            Status = ConnectionStatus.Connected;
            ClearPendingMessages();

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
        var variablesUri = new Uri($"http://{Endpoint.ToUriString()}/variables.html");
        var variableRegex = new Regex(@"<p id=""(?<name>.+?)"">(?<value>.+?)<\/p>", RegexOptions.Compiled);
        var playerState = new PlayerState();

        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(200, token);

                var response = await UnwrapTimeout(() => client.GetAsync(variablesUri, token));
                if (response == null)
                    continue;

                response.EnsureSuccessStatusCode();
                var message = await response.Content.ReadAsStringAsync(token);

                Logger.Trace("Received \"{0}\" from \"{1}\"", message, Name);
                var variables = variableRegex.Matches(message).NotNull().ToDictionary(m => m.Groups["name"].Value, m => m.Groups["value"].Value);

                if (variables.TryGetValue("state", out var stateString) && int.TryParse(stateString, out var state) && state != playerState.State)
                {
                    PublishMessage(new MediaPlayingChangedMessage(state == 2));
                    playerState.State = state;
                }

                if (playerState.State < 0)
                    continue;

                if (variables.TryGetValue("filepath", out var path))
                {
                    if (string.IsNullOrWhiteSpace(path))
                        path = null;

                    if (path != playerState.Path)
                    {
                        PublishMessage(new MediaPathChangedMessage(path));
                        playerState.Path = path;
                    }
                }

                if (variables.TryGetValue("duration", out var durationString) && long.TryParse(durationString, out var duration) && duration >= 0 && duration != playerState.Duration)
                {
                    PublishMessage(new MediaDurationChangedMessage(TimeSpan.FromMilliseconds(duration)));
                    playerState.Duration = duration;
                }

                if (variables.TryGetValue("position", out var positionString) && long.TryParse(positionString, out var position) && position >= 0 && position != playerState.Position)
                {
                    PublishMessage(new MediaPositionChangedMessage(TimeSpan.FromMilliseconds(position)));
                    playerState.Position = position;
                }

                if (variables.TryGetValue("playbackrate", out var playbackrateString) && double.TryParse(playbackrateString, out var speed) && speed > 0 && speed != playerState.Speed)
                {
                    PublishMessage(new MediaSpeedChangedMessage(speed));
                    playerState.Speed = speed;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task WriteAsync(HttpClient client, CancellationToken token)
    {
        var uriBase = $"http://{Endpoint.ToUriString()}/";

        try
        {
            while (!token.IsCancellationRequested)
            {
                await WaitForMessageAsync(token);
                var message = await ReadMessageAsync(token);

                var uriFile = message switch
                {
                    MediaChangePathMessage _ => "browser.html?path=",
                    _ => "command.html?wm_command="
                };

                var uriArguments = message switch
                {
                    MediaPlayPauseMessage playPauseMessage => $"{(int)(playPauseMessage.ShouldBePlaying ? MpcCommand.Play : MpcCommand.Pause)}",
                    MediaSeekMessage seekMessage => $"{(int)MpcCommand.Seek}&position={seekMessage.Position:hh\\:mm\\:ss}",
                    MediaChangePathMessage changePathMessage => string.IsNullOrWhiteSpace(changePathMessage.Path) ? null : $"{Uri.EscapeDataString(changePathMessage.Path)}",
                    _ => null
                };

                if (uriArguments == null)
                    continue;

                var requestUri = new Uri($"{uriBase}{uriFile}{uriArguments}");
                Logger.Trace("Sending \"{0}{1}\" to \"{2}\"", uriFile, uriArguments, Name);

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
        }
        else if (action == SettingsAction.Loading)
        {
            if (settings.TryGetValue<EndPoint>(nameof(Endpoint), out var endpoint))
                Endpoint = endpoint;
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
        public string Path { get; set; }
        public long? Position { get; set; }
        public double? Speed { get; set; }
        public int? State { get; set; }
        public long? Duration { get; set; }
    }

    private enum MpcCommand
    {
        Seek = -1,
        Play = 887,
        Pause = 888,
    }
}
