namespace LightBrowser;

// Best-effort YouTube ad blocking, the same ideas Brave and uBlock Origin use, run inside the page:
//  1. Strip the ad data (adPlacements, playerAds, adSlots) out of YouTube's player responses before the player
//     sees them, both the data embedded in the page and the replies to its own requests.
//  2. Remove ad items from feeds, search results and the Shorts queue the same way, so no empty ad slots appear.
//  3. Hide ad slots and banners with CSS.
//  4. Fallback: if an ad still starts, click Skip, or mute it and jump to its end, then restore sound and speed.
//  5. Stay out of YouTube's detection: stop its "abnormality" callback from firing, remove the "ad blockers are not
//     allowed" pop-up, and reload the video (twice at most) if the player refuses to play after a block.
// Network blocking cannot do this: YouTube's ads come from the same servers as its videos.
// YouTube changes its player often and fights ad blockers, so this may stop working without warning. Offline checks:
// tools\check-youtube.ps1 -SelfTest. Against the real site: tools\check-youtube.ps1. Only acts on YouTube addresses.
public static class YouTubeAds
{
    public static bool IsYouTubeHost(string? host) =>
        host != null && (host == "youtube.com" || host.EndsWith(".youtube.com") ||
                         host == "youtube-nocookie.com" || host.EndsWith(".youtube-nocookie.com") ||
                         host == "youtubekids.com" || host.EndsWith(".youtubekids.com"));

    public const string Css =
        "#masthead-ad, #player-ads, .ytd-display-ad-renderer, ytd-ad-slot-renderer, ytd-in-feed-ad-layout-renderer, " +
        "ytd-promoted-sparkles-web-renderer, ytd-promoted-video-renderer, ytd-banner-promo-renderer, ytd-statement-banner-renderer, " +
        "ytd-companion-slot-renderer, ytd-action-companion-ad-renderer, ytd-player-legacy-desktop-watch-ads-renderer, " +
        "ytm-promoted-sparkles-web-renderer, ytm-companion-slot, ytm-promoted-video-renderer, " +
        ".ytp-ad-overlay-container, .ytp-ad-overlay-slot, ytd-rich-item-renderer:has(> #content > ytd-ad-slot-renderer) " +
        "{ display: none !important; }";

    // The page script, with the style rules filled in. Runs at the start of every document (all frames).
    public static readonly string Script = Template.Replace("__CSS__", Css);

    const string Template = @"(function () {
  'use strict';
  if (!/(^|\.)(youtube(-nocookie)?|youtubekids)\.com$/.test(location.hostname)) return;
  if (window.__lbAdBlock) return;
  var stats = window.__lbAdBlock = { pruned: 0, feedItems: 0, skipped: 0, jumped: 0, popups: 0, recovered: 0, neutralised: 0 };
  var AD_KEYS = ['adPlacements', 'playerAds', 'adSlots'];
  var AD_ITEMS = ['adSlotRenderer', 'promotedSparklesWebRenderer', 'promotedSparklesTextSearchRenderer', 'searchPyvRenderer',
                  'displayAdRenderer', 'compactPromotedVideoRenderer', 'promotedVideoRenderer', 'bannerPromoRenderer',
                  'statementBannerRenderer', 'inFeedAdLayoutRenderer', 'actionCompanionAdRenderer'];
  var hasOwn = Object.prototype.hasOwnProperty;

  function dropKeys(o) {
    for (var k = 0; k < AD_KEYS.length; k++) {
      if (hasOwn.call(o, AD_KEYS[k])) { try { delete o[AD_KEYS[k]]; stats.pruned++; } catch (e) {} }
    }
  }

  // Cheap: the ad keys at the top of an object, of its playerResponse, and of each item of an array.
  function prune(o) {
    try {
      if (!o || typeof o !== 'object') return o;
      if (Array.isArray(o)) { for (var i = 0; i < o.length; i++) prune(o[i]); return o; }
      if (o.playerResponse) prune(o.playerResponse);
      dropKeys(o);
    } catch (e) {}
    return o;
  }

  // One entry of a feed, a search result list or the Shorts queue that is an advert.
  function isAdItem(x) {
    if (!x || typeof x !== 'object') return false;
    var i;
    for (i = 0; i < AD_ITEMS.length; i++) if (hasOwn.call(x, AD_ITEMS[i])) return true;
    var content = x.richItemRenderer && x.richItemRenderer.content;
    if (content && typeof content === 'object') for (i = 0; i < AD_ITEMS.length; i++) if (hasOwn.call(content, AD_ITEMS[i])) return true;
    var reel = x.command && x.command.reelWatchEndpoint && x.command.reelWatchEndpoint.adClientParams;
    return !!(reel && reel.isAd);
  }

  // Thorough: walks a whole API response (bounded depth), dropping ad keys and ad entries wherever they are.
  function walk(o, depth) {
    try {
      if (!o || typeof o !== 'object' || (depth || 0) > 14) return o;
      depth = (depth || 0) + 1;
      if (Array.isArray(o)) {
        for (var i = o.length - 1; i >= 0; i--) {
          if (isAdItem(o[i])) { o.splice(i, 1); stats.feedItems++; } else walk(o[i], depth);
        }
        return o;
      }
      dropKeys(o);
      for (var key in o) if (hasOwn.call(o, key)) { var v = o[key]; if (v && typeof v === 'object') walk(v, depth); }
    } catch (e) {}
    return o;
  }
  stats.prune = prune;
  stats.walk = walk;

  var parse = JSON.parse;
  JSON.parse = function () { return prune(parse.apply(this, arguments)); };
  if (window.Response && Response.prototype.json) {
    var json = Response.prototype.json;
    Response.prototype.json = function () {
      var api = /\/youtubei\/v1\//.test(this.url || '');
      return json.apply(this, arguments).then(function (data) { return api ? walk(data) : prune(data); });
    };
  }
  function guard(name, clean) {
    var value;
    try {
      Object.defineProperty(window, name, { configurable: true, get: function () { return value; }, set: function (v) { value = clean(v); } });
    } catch (e) {}
  }
  guard('ytInitialPlayerResponse', function (v) { return walk(v); });
  guard('ytInitialData', function (v) { return walk(v); });

  // YouTube checks whether its player data was tampered with and calls onAbnormalityDetected; make that a no-op.
  try {
    var then = Promise.prototype.then;
    Promise.prototype.then = function (onOk, onErr) {
      if (typeof onOk === 'function') {
        var text = '';
        try { text = Function.prototype.toString.call(onOk); } catch (e) {}
        if (text.indexOf('onAbnormalityDetected') !== -1) { onOk = function () {}; stats.neutralised++; }
      }
      return then.call(this, onOk, onErr);
    };
  } catch (e) {}

  function addCss() {
    try {
      var s = document.createElement('style');
      s.textContent = '__CSS__';
      (document.head || document.documentElement).appendChild(s);
    } catch (e) {}
  }
  if (document.documentElement) addCss(); else document.addEventListener('DOMContentLoaded', addCss);

  var saved = null, retries = 0, retryAt = 0, retryFor = '';
  function tick() {
    try {
      var p = document.querySelector('.html5-video-player');
      var v = document.querySelector('video.html5-main-video') || document.querySelector('video');
      var adShowing = !!(p && (p.classList.contains('ad-showing') || p.classList.contains('ad-interrupting')) &&
          p.querySelector('.ytp-ad-player-overlay, .ytp-ad-player-overlay-layout, .ytp-ad-text, .ytp-ad-preview-container, .ytp-ad-skip-button-container'));
      if (adShowing) {
        var skip = document.querySelector('.ytp-skip-ad-button, .ytp-ad-skip-button, .ytp-ad-skip-button-modern, .ytp-ad-skip-button-slot button');
        if (skip) { skip.click(); stats.skipped++; }
        if (v && isFinite(v.duration) && v.duration > 0 && v.currentTime < v.duration - 0.3) {
          if (!saved) saved = { muted: v.muted, rate: v.playbackRate };
          v.muted = true; v.playbackRate = 16;
          try { v.currentTime = v.duration; stats.jumped++; } catch (e) {}
        }
      } else if (saved) {
        // The ad is over: give the video its own sound and speed back (an ad must not leave it muted or at 16x).
        if (v) { v.muted = saved.muted; v.playbackRate = saved.rate >= 2 ? 1 : saved.rate; }
        saved = null;
      }

      var enforcement = document.querySelector('ytd-enforcement-message-view-model');
      if (enforcement) {
        var dialog = enforcement.closest('tp-yt-paper-dialog') || enforcement;
        dialog.remove();
        var backdrops = document.querySelectorAll('tp-yt-iron-overlay-backdrop');
        for (var b = 0; b < backdrops.length; b++) backdrops[b].remove();
        if (v && v.paused) { try { v.play(); } catch (e) {} }
        stats.popups++;
      }

      // The player shows an error after refusing to play: try the same video again, at most twice per video.
      if (p && p.querySelector('.ytp-error') && typeof p.loadVideoById === 'function' && typeof p.getVideoData === 'function') {
        var data = p.getVideoData();
        var id = data && data.video_id;
        if (id && id !== retryFor) { retryFor = id; retries = 0; }
        if (id && retries < 2 && Date.now() - retryAt > 8000) {
          retries++; retryAt = Date.now(); stats.recovered++;
          p.loadVideoById(id, typeof p.getCurrentTime === 'function' ? p.getCurrentTime() : 0);
        }
      }
    } catch (e) {}
  }
  setInterval(tick, 250);
})();";
}
