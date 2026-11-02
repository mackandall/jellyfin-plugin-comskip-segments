using System;
using MediaBrowser.Common.Api;
using Jellyfin.Plugin.ComskipSegments.RepeatAds;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ComskipSegments.Web;

/// <summary>The settings page's line about the repeat-ad check's kept fingerprints.</summary>
[ApiController]
[Route("ComskipSegments/RepeatAds")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class RepeatAdsController : ControllerBase
{
    private readonly FingerprintStore _store;

    public RepeatAdsController(FingerprintStore store)
    {
        _store = store;
    }

    public sealed record StatusDto(int Count, long Bytes, DateTime? OldestUtc);

    [HttpGet("Status")]
    public ActionResult<StatusDto> Status()
    {
        var (count, bytes, oldest) = _store.Summary();
        return new StatusDto(count, bytes, oldest);
    }
}
