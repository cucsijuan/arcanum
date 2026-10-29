// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Arcanum.Data.Career;
using Godot;
using HttpClient = System.Net.Http.HttpClient;

namespace Arcanum.Client;

/// <summary>
/// The career service's check of the career (docs/career-mode.md, *Trust*): after the cloud copy, the actions it hasn't
/// checked go to it; it replays them with the rules and its own clock and seals them. Online events need a checked
/// career. A career the service doesn't accept keeps playing here; the player may go back to the checked one.
/// </summary>
public sealed partial class CareerService
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Where the career stands with the career service, for the player.</summary>
    public string CheckStatus { get; private set; } = "";

    /// <summary>The service has a checked career that this one went apart from (its actions weren't accepted, or another device's were).</summary>
    public bool CheckedCareerDiffers { get; private set; }

    /// <summary>Whether the career service checked every action of the career (needed for online events).</summary>
    public bool IsChecked => _save is { Seal: { } seal } save && seal.Count == save.Journal.Count && CareerSeals.Covers(seal, save.Journal);

    private static HttpClient Service(string token)
    {
        var http = new HttpClient { BaseAddress = Account.ServiceUrl, Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    /// <summary>Sends what the service hasn't checked; true when the career changed (its seal), so the cloud gets it too.</summary>
    private async Task<bool> CheckOnce()
    {
        if (_save is not { } save || save.Journal.Count == 0 || CareerSeals.PublicKeys.Count == 0) return false;
        int checkedCount = save.Seal is { } seal && CareerSeals.Covers(seal, save.Journal) ? seal.Count : 0;
        if (checkedCount == save.Journal.Count)
        {
            SetCheck("Checked by the career service.", differs: false);
            return false;
        }
        if (await App.Instance.Online.IdTokenAsync() is not { } token)
        {
            SetCheck("Not checked by the career service: sign in with Discord (online events need a checked career).", differs: false);
            return false;
        }
        using var http = Service(token);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var actions = save.Journal.Skip(checkedCount).ToList();
            // The states kept at version changes go only for the actions not checked yet.
            var versions = save.Versions.Select(v => v.From > checkedCount ? v : v with { Before = null }).ToList();
            var request = new CareerSyncRequest(_contentId, OnlineService.Version, checkedCount, CareerSeals.Hash(save.Journal, checkedCount), actions,
                checkedCount == 0 ? save.State : null, versions);
            using var reply = await http.PostAsJsonAsync("career/sync", request, Web);
            if (!reply.IsSuccessStatusCode)
            {
                SetCheck($"Not checked by the career service ({(int)reply.StatusCode}).", differs: false);
                return false;
            }
            var response = await reply.Content.ReadFromJsonAsync<CareerSyncResponse>(Web) ?? throw new InvalidDataException("The career service's answer is empty.");
            var genuine = response.Seal is { } s && s.Count <= save.Journal.Count && CareerSeals.Covers(s, save.Journal) ? s : null;
            switch (response.Status)
            {
                case CareerSyncStatus.Ok when genuine is not null:
                    save.Seal = genuine;
                    DropCheckedVersionStates(save);
                    Write();
                    SetCheck("Checked by the career service.", differs: false);
                    return true;
                case CareerSyncStatus.Conflict when genuine is not null && genuine.Count > checkedCount:
                    checkedCount = genuine.Count; // another device's sync checked more of this same career: build on it
                    continue;
                case CareerSyncStatus.Rejected:
                    if (genuine is not null) { save.Seal = genuine; DropCheckedVersionStates(save); Write(); }
                    SetCheck($"The career service doesn't accept this career's latest actions: {response.Reason} You can go back to the checked career.", differs: true);
                    return genuine is not null;
                case CareerSyncStatus.Unsupported:
                    SetCheck($"Not checked yet: {response.Reason}", differs: false);
                    return false;
                default:
                    SetCheck("The career service has a checked career that this one went apart from. You can go back to the checked career.", differs: true);
                    return false;
            }
        }
        return false;
    }

    /// <summary>
    /// Replaces the career here with the one the career service checked (this device's files are kept in a backup zip).
    /// </summary>
    public async Task<bool> TakeCheckedCareerAsync()
    {
        if (await App.Instance.Online.IdTokenAsync() is not { } token) return false;
        using var http = Service(token);
        using var reply = await http.GetAsync("career/profile");
        if (!reply.IsSuccessStatusCode) return false;
        var profile = await reply.Content.ReadFromJsonAsync<CareerProfile>(Web);
        if (profile is null || profile.Seal.Count != profile.Journal.Count || !CareerSeals.Covers(profile.Seal, profile.Journal)) return false;
        if (LocalBundle() is { } local) Backup(local, "unchecked");
        _save = new CareerSave { Content = _contentId, Journal = profile.Journal, State = profile.State, Seal = profile.Seal,
            Versions = { new CareerVersion(profile.Journal.Count, OnlineService.Version, _contentId) } };
        Write();
        _events = null;
        Problem = null;
        Load();
        SetCheck("Checked by the career service.", differs: false);
        SyncSoon();
        return true;
    }

    /// <summary>The states kept at version changes the service has checked past are no longer needed.</summary>
    private static void DropCheckedVersionStates(CareerSave save)
    {
        for (int i = 0; i < save.Versions.Count; i++)
            if (save.Versions[i] is { Before: not null } v && v.From <= save.Seal!.Count) save.Versions[i] = v with { Before = null };
    }

    private void SetCheck(string status, bool differs)
    {
        if (status != CheckStatus) GD.Print($"Career: {status}");
        CheckStatus = status;
        CheckedCareerDiffers = differs;
        CloudChanged?.Invoke();
    }
}
