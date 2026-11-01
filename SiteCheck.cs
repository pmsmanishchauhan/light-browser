using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LightBrowser;

public sealed record SiteToCheck(string Name, string Url);

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
