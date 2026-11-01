using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LightBrowser;

public sealed record SiteToCheck(string Name, string Url);

public sealed record SelfTestResult(string Name, bool Ok, string Detail);

// Developer tool: "LightBrowser.exe --check-youtube-selftest <out.json>" loads the YouTube ad script into a blank page
// (pretending to be on youtube.com) and runs it against made-up YouTube data and a made-up player. No network.
// It proves the script's logic, not that YouTube still accepts it; the real-site probe does that.
public sealed class YouTubeSelfTestWindow : Window
{
    const string Tests = @"(async function () {
  var api = window.__lbAdBlock, out = [];
  function t(name, ok, detail) { out.push({ Name: name, Ok: !!ok, Detail: detail || '' }); }
  function has(o, k) { return Object.prototype.hasOwnProperty.call(o, k); }
  function wait(ms) { return new Promise(function (r) { setTimeout(r, ms); }); }
  t('the script installed itself', api && typeof api.walk === 'function' && typeof api.prune === 'function');
  if (!api) return JSON.stringify(out);

  var a = { adPlacements: [1], playerAds: [1], adSlots: [1], videoDetails: { videoId: 'x' }, streamingData: { y: 1 } };
  api.prune(a);
  t('ad keys are removed and the rest is kept', !has(a, 'adPlacements') && !has(a, 'playerAds') && !has(a, 'adSlots') && a.videoDetails.videoId === 'x' && a.streamingData.y === 1);

  var b = { playerResponse: { adPlacements: [1], videoDetails: { videoId: 'x' } } };
  api.prune(b);
  t('ad keys inside playerResponse are removed', !has(b.playerResponse, 'adPlacements') && b.playerResponse.videoDetails.videoId === 'x');

  var c = [{ playerResponse: { adSlots: [1] } }, { response: { ok: 1 } }];
  api.prune(c);
  t('an array of responses is handled', !has(c[0].playerResponse, 'adSlots') && c[1].response.ok === 1);

  var parsed = JSON.parse('{""adPlacements"":[1],""title"":""keep""}');
  t('JSON.parse results are cleaned', !has(parsed, 'adPlacements') && parsed.title === 'keep');

  var feed = { contents: [
    { richItemRenderer: { content: { videoRenderer: { id: 1 } } } },
    { richItemRenderer: { content: { adSlotRenderer: {} } } },
    { adSlotRenderer: {} },
    { itemSectionRenderer: { contents: [ { videoRenderer: { id: 2 } }, { promotedSparklesWebRenderer: {} }, { searchPyvRenderer: {} } ] } } ] };
  api.walk(feed);
  t('ad items are removed from feeds, nothing else', feed.contents.length === 2 && feed.contents[0].richItemRenderer.content.videoRenderer.id === 1
    && feed.contents[1].itemSectionRenderer.contents.length === 1 && feed.contents[1].itemSectionRenderer.contents[0].videoRenderer.id === 2,
    JSON.stringify(feed));

  var reels = { entries: [ { command: { reelWatchEndpoint: { videoId: 'ad', adClientParams: { isAd: true } } } },
                           { command: { reelWatchEndpoint: { videoId: 'real' } } },
                           { command: { reelWatchEndpoint: { videoId: 'real2', adClientParams: { isAd: false } } } } ] };
  api.walk(reels);
  t('Shorts ads are removed and real Shorts stay', reels.entries.length === 2 && reels.entries[0].command.reelWatchEndpoint.videoId === 'real'
    && reels.entries[1].command.reelWatchEndpoint.videoId === 'real2');

  var deep = { a: { b: { c: [ { adPlacements: [1], keep: 1 } ] } } };
  api.walk(deep);
  t('ad keys deep in a response are removed', !has(deep.a.b.c[0], 'adPlacements') && deep.a.b.c[0].keep === 1);

  var safe = true, cyc = {}; cyc.self = cyc;
  var tower = {}, cur = tower; for (var d = 0; d < 200; d++) { cur.next = {}; cur = cur.next; }
  try { api.walk(cyc); api.walk(tower); api.walk(null); api.walk(5); api.walk('x'); api.walk(undefined); api.prune(undefined); api.prune(3); } catch (e) { safe = false; }
  t('odd input (cycles, 200 levels, null, numbers) is survived', safe);

  window.ytInitialPlayerResponse = { adPlacements: [1], videoDetails: { videoId: 'v' } };
  t('data assigned to ytInitialPlayerResponse is cleaned', !has(window.ytInitialPlayerResponse, 'adPlacements') && window.ytInitialPlayerResponse.videoDetails.videoId === 'v');
  window.ytInitialData = { contents: [ { adSlotRenderer: {} }, { videoRenderer: { id: 9 } } ] };
  t('data assigned to ytInitialData is cleaned', window.ytInitialData.contents.length === 1 && window.ytInitialData.contents[0].videoRenderer.id === 9);

  var r1 = await new Response('{""adSlots"":[1],""ok"":1}').json();
  t('Response.json results are cleaned', !has(r1, 'adSlots') && r1.ok === 1);
  var resp = new Response('{""contents"":[{""adSlotRenderer"":{}},{""videoRenderer"":{""id"":3}}]}');
  Object.defineProperty(resp, 'url', { value: 'https://www.youtube.com/youtubei/v1/browse?prettyPrint=false' });
  var r2 = await resp.json();
  t('YouTube API responses are cleaned thoroughly', r2.contents.length === 1 && r2.contents[0].videoRenderer.id === 3);

  var abnormal = false, normal = false;
  await Promise.resolve(1).then(function onAbnormalityDetected() { abnormal = true; });
  await Promise.resolve(1).then(function () { normal = true; });
  t('the abnormality callback is silenced, ordinary callbacks still run', !abnormal && normal && api.neutralised >= 1);

  var styles = Array.prototype.map.call(document.querySelectorAll('style'), function (s) { return s.textContent; }).join(' ');
  t('the style rules were added', styles.indexOf('ytd-ad-slot-renderer') !== -1 && styles.indexOf('display: none !important') !== -1);

  // A made-up player, to test the fallback without YouTube.
  var player = document.createElement('div'); player.className = 'html5-video-player ad-showing';
  var video = document.createElement('video'); video.className = 'html5-main-video';
  var time = 0, sets = [];
  Object.defineProperty(video, 'duration', { value: 30 });
  Object.defineProperty(video, 'currentTime', { get: function () { return time; }, set: function (x) { sets.push(x); time = x; } });
  player.appendChild(video); document.body.appendChild(player);
  await wait(700);
  t('with no ad controls on screen the video is left alone', sets.length === 0 && !video.muted && video.playbackRate === 1,
    'sets=' + sets.length + ' muted=' + video.muted + ' rate=' + video.playbackRate);

  var overlay = document.createElement('div'); overlay.className = 'ytp-ad-player-overlay'; player.appendChild(overlay);
  await wait(700);
  t('a real ad is jumped to its end, muted and fast', sets.length >= 1 && sets[0] === 30 && video.muted && video.playbackRate === 16,
    'sets=' + sets.join(',') + ' muted=' + video.muted + ' rate=' + video.playbackRate);

  player.classList.remove('ad-showing'); overlay.remove();
  await wait(700);
  t('when the ad ends the video gets its sound and speed back', !video.muted && video.playbackRate === 1,
    'muted=' + video.muted + ' rate=' + video.playbackRate);

  var skipButton = document.createElement('button'); skipButton.className = 'ytp-skip-ad-button';
  var clicked = 0; skipButton.addEventListener('click', function () { clicked++; });
  document.body.appendChild(skipButton);
  player.className = 'html5-video-player ad-showing'; player.appendChild(overlay); time = 0;
  await wait(700);
  t('the Skip button is clicked when an ad shows one', clicked >= 1);
  skipButton.remove(); overlay.remove(); player.remove();

  var enforcement = document.createElement('tp-yt-paper-dialog'); enforcement.appendChild(document.createElement('ytd-enforcement-message-view-model'));
  document.body.appendChild(enforcement);
  await wait(700);
  t('the ad-blocker pop-up is removed', !document.body.contains(enforcement) && api.popups >= 1);

  return JSON.stringify(out);
})()";

    readonly string _output;
    readonly WebView2 _view = new();

    public YouTubeSelfTestWindow(string output)
    {
        _output = output;
        Title = "LightBrowser YouTube self-test";
        Width = 900; Height = 600;
        Content = _view;
        Loaded += async (_, _) =>
        {
            try { await RunAsync(); }
            catch (Exception ex) { File.WriteAllText(_output + ".error", ex.ToString()); }
            finally { Application.Current.Shutdown(); }
        };
    }

    async Task RunAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(ProfileStore.Root, "WebView2Data"));
        await _view.EnsureCoreWebView2Async(env);
        var web = _view.CoreWebView2;
        var loaded = new TaskCompletionSource();
        web.NavigationCompleted += (_, _) => loaded.TrySetResult();
        web.NavigateToString("<html><body></body></html>");
        await loaded.Task;

        // The script only runs on YouTube addresses; here it is told it is on one.
        await web.ExecuteScriptAsync(YouTubeAds.Script.Replace("location.hostname", "'www.youtube.com'"));
        // ExecuteScriptAsync does not wait for a promise, so the tests store their answer and it is polled for.
        await web.ExecuteScriptAsync("window.__lbDone = null;");
        await web.ExecuteScriptAsync(Tests + ".then(function (r) { window.__lbDone = r; }, function (e) { window.__lbDone = JSON.stringify([{ Name: 'the tests ran', Ok: false, Detail: String(e) }]); });");
        var json = "null";
        for (var waited = 0; waited < 60 && json == "null"; waited++)
        {
            await Task.Delay(500);
            json = await web.ExecuteScriptAsync("window.__lbDone");
        }
        var results = JsonSerializer.Deserialize<List<SelfTestResult>>(JsonSerializer.Deserialize<string>(json) ?? "[]") ?? new();
        File.WriteAllText(_output, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed record YouTubeSample(int Ms, bool Ad, bool AdUi, double Time, double Duration, bool Paused, bool Enforcement, string Title, string Stats);

// Developer tool: "LightBrowser.exe --check-youtube <video url> <out.json> [--no-block] [--seconds N]" plays a video in
// this browser's engine (sound off) and samples the player twice a second: is an ad showing, is the video moving,
// did YouTube show its "ad blockers are not allowed" pop-up. --no-block runs the same video without the blocker,
// which shows whether YouTube serves ads to this machine at all (without that control a clean run proves nothing).
public sealed class YouTubeProbeWindow : Window
{
    const string Probe =
        "(function(){ var p = document.querySelector('.html5-video-player'); var v = document.querySelector('video.html5-main-video') || document.querySelector('video');" +
        " return JSON.stringify({ ad: !!(p && (p.classList.contains('ad-showing') || p.classList.contains('ad-interrupting')))," +
        " adUi: !!document.querySelector('.ytp-ad-player-overlay, .ytp-ad-player-overlay-layout, .ytp-ad-text, .ytp-ad-preview-container')," +
        " t: v ? v.currentTime : -1, dur: v && isFinite(v.duration) ? v.duration : -1, paused: v ? v.paused : true," +
        " enforcement: !!document.querySelector('ytd-enforcement-message-view-model'), title: document.title," +
        " stats: window.__lbAdBlock ? JSON.stringify(window.__lbAdBlock) : '' }); })()";

    readonly string _url, _output;
    readonly bool _block;
    readonly int _seconds;
    readonly WebView2 _view = new();

    public YouTubeProbeWindow(string url, string output, bool block, int seconds)
    {
        _url = url; _output = output; _block = block; _seconds = seconds;
        Title = "LightBrowser YouTube probe";
        Width = 1200; Height = 800;
        Content = _view;
        Loaded += async (_, _) =>
        {
            try { await RunAsync(); }
            catch (Exception ex) { File.WriteAllText(_output + ".error", ex.ToString()); }
            finally { Application.Current.Shutdown(); }
        };
    }

    async Task RunAsync()
    {
        var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required --mute-audio" };
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(ProfileStore.Root, "WebView2Data"), options);
        await _view.EnsureCoreWebView2Async(env);
        var web = _view.CoreWebView2;
        var blocker = RequestBlocker.Load();
        var blockedRequests = 0;
        if (_block)
        {
            await web.AddScriptToExecuteOnDocumentCreatedAsync(YouTubeAds.Script);
            web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            web.WebResourceRequested += (_, a) =>
            {
                if (!blocker.IsBlocked(a.Request.Uri)) return;
                blockedRequests++;
                a.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "");
            };
        }

        web.Navigate(_url);
        var samples = new List<YouTubeSample>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(_seconds))
        {
            await Task.Delay(500);
            try
            {
                var json = await web.ExecuteScriptAsync(Probe);
                var info = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Deserialize<string>(json) ?? "{}");
                samples.Add(new YouTubeSample((int)clock.ElapsedMilliseconds, info.GetProperty("ad").GetBoolean(), info.GetProperty("adUi").GetBoolean(),
                    info.GetProperty("t").GetDouble(), info.GetProperty("dur").GetDouble(), info.GetProperty("paused").GetBoolean(),
                    info.GetProperty("enforcement").GetBoolean(), info.GetProperty("title").GetString() ?? "", info.GetProperty("stats").GetString() ?? ""));
            }
            catch (Exception) { /* the page is between documents */ }
        }

        var adSamples = samples.Where(s => s.Ad).ToList();
        var contentMoved = samples.Where(s => !s.Ad && s.Time > 0).Select(s => s.Time).DefaultIfEmpty(0).Max();
        var summary = new
        {
            Block = _block, Url = _url, Seconds = _seconds, Samples = samples.Count, BlockedRequests = blockedRequests,
            AdSeen = adSamples.Count > 0, AdSamples = adSamples.Count, FirstAdMs = adSamples.FirstOrDefault()?.Ms ?? -1,
            AdSeenAtEnd = samples.LastOrDefault()?.Ad ?? false, ContentSecondsReached = Math.Round(contentMoved, 1),
            EnforcementPopup = samples.Any(s => s.Enforcement), Title = samples.LastOrDefault()?.Title ?? "",
            FinalStats = samples.LastOrDefault()?.Stats ?? "", Timeline = samples,
        };
        File.WriteAllText(_output, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed record SiteResult(string Name, string Url, string FinalUrl, int Status, bool Loaded, string Error, string Title,
                                bool PasswordField, bool LoginField, string Flag, int Ms);

// Developer tool: "LightBrowser.exe --check-sites in.json out.json" opens each address in this browser's own engine,
// one after another, and writes what happened (status, final address, title, whether a login form showed up, whether
// the site put up a bot check). It runs on the data folder you point LIGHTBROWSER_HOME at, so use a throwaway one.
public sealed class SiteCheckWindow : Window
{
    const string Probe =
        "(function(){ const vis = e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; };" +
        " const pw = Array.from(document.querySelectorAll('input[type=password]')).some(vis);" +
        " const id = Array.from(document.querySelectorAll('input[type=email],input[type=text],input[type=tel],input[name*=user i],input[name*=login i],input[name*=email i],input[autocomplete=username]')).some(vis);" +
        " const body = ((document.body && document.body.innerText) || '').slice(0, 400).replace(/\\s+/g, ' ');" +
        " return JSON.stringify({ pw: pw, id: id, title: document.title, url: location.href, body: body }); })()";

    static readonly Regex Challenge = new(
        @"verify you are human|just a moment|checking your browser|access denied|captcha|attention required|unusual traffic|not a robot|request blocked|enable javascript and cookies",
        RegexOptions.IgnoreCase);

    static readonly Regex Embedded = new(@"browser or app may not be secure|not supported|unsupported browser|update your browser", RegexOptions.IgnoreCase);

    readonly string _input, _output;
    readonly WebView2 _view = new();

    public SiteCheckWindow(string input, string output)
    {
        _input = input; _output = output;
        Title = "LightBrowser site check";
        Width = 1200; Height = 800;
        Content = _view;
        Loaded += async (_, _) =>
        {
            try { await RunAsync(); }
            catch (Exception ex) { File.WriteAllText(_output + ".error", ex.ToString()); }
            finally { Application.Current.Shutdown(); }
        };
    }

    async Task RunAsync()
    {
        // "catalog" checks the built-in OTA portals and search engines; otherwise a JSON list of { Name, Url }.
        var sites = _input == "catalog"
            ? OtaCatalog.All.Select(p => new SiteToCheck(p.Name, p.Url))
                .Concat(SearchEngines.All.Select(e => new SiteToCheck("search: " + e.Name, e.UrlFor("test")))).ToList()
            : JsonSerializer.Deserialize<List<SiteToCheck>>(File.ReadAllText(_input)) ?? new();
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(ProfileStore.Root, "WebView2Data"));
        await _view.EnsureCoreWebView2Async(env);
        var web = _view.CoreWebView2;
        var results = new List<SiteResult>();

        foreach (var site in sites)
        {
            var done = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>();
            void OnDone(object? s, CoreWebView2NavigationCompletedEventArgs a) => done.TrySetResult(a);
            web.NavigationCompleted += OnDone;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            string error = "", title = "", final = "", flag = "";
            int status = 0; bool loaded = false, pw = false, id = false;
            try
            {
                web.Navigate(site.Url);
                var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(40)));
                if (finished != done.Task) error = "timed out after 40 s";
                else
                {
                    var args = done.Task.Result;
                    status = args.HttpStatusCode;
                    loaded = args.IsSuccess;
                    if (!loaded) error = args.WebErrorStatus.ToString();
                    await Task.Delay(3500);   // let a script-built login form appear
                    var json = await web.ExecuteScriptAsync(Probe);
                    var info = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Deserialize<string>(json) ?? "{}");
                    pw = info.GetProperty("pw").GetBoolean();
                    id = info.GetProperty("id").GetBoolean();
                    title = info.GetProperty("title").GetString() ?? "";
                    final = info.GetProperty("url").GetString() ?? "";
                    var body = title + " " + (info.GetProperty("body").GetString() ?? "");
                    if (Challenge.IsMatch(body)) flag = "bot check or block page";
                    else if (Embedded.IsMatch(body)) flag = "refuses embedded browsers";
                }
            }
            catch (Exception ex) { error = ex.Message; }
            finally { web.NavigationCompleted -= OnDone; }
            results.Add(new SiteResult(site.Name, site.Url, final, status, loaded, error, title, pw, id, flag, (int)clock.ElapsedMilliseconds));
            try { web.Navigate("about:blank"); await Task.Delay(300); } catch (Exception) { }
        }
        File.WriteAllText(_output, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
}
