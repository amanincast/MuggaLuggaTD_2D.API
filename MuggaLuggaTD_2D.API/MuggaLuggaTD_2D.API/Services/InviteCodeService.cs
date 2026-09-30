using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MuggaLuggaTD_2D.API.Data;
using MuggaLuggaTD_2D.API.Models;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>
/// Invite codes: registration is open only to people holding one while
/// <c>Registration:RequireInviteCode</c> is on (the default; Development turns it off).
///
/// <para>Codes are minted and managed from the command line, in any environment - on the server,
/// <c>docker compose exec api dotnet MuggaLuggaTD_2D.API.dll invite-codes --count 5 --note "Sam"</c>:</para>
/// <code>
/// invite-codes [--count N] [--uses N] [--days N] [--note "who"]   mint N codes (default 1 use, no expiry)
/// invite-codes --list                                            every code and how far it is used
/// invite-codes --revoke CODE                                     admit nobody new with it
/// </code>
///
/// <para>A code is <b>spent before the account is made and refunded if making it fails</b>, so a
/// failed registration (a taken email, a weak password) does not use up a tester's single-use code.</para>
/// </summary>
public class InviteCodeService
{
    public const string Command = "invite-codes";

    /// <summary>No 0/O, 1/I/L, or U: codes are read aloud and typed from a message.</summary>
    private const string Alphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";

    private readonly ApplicationDbContext _context;
    private readonly TimeProvider _clock;

    public InviteCodeService(ApplicationDbContext context, TimeProvider clock)
    {
        _context = context;
        _clock = clock;
    }

    public enum RedeemResult { Redeemed, Missing, Unknown, Revoked, Expired, UsedUp }

    /// <summary>Upper case, spaces dropped, and the dash put back if it was left out.</summary>
    public static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;
        var sb = new StringBuilder();
        foreach (char c in code.ToUpperInvariant())
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.Length == 8 ? sb.ToString(0, 4) + "-" + sb.ToString(4, 4) : sb.ToString();
    }

    public static string Describe(RedeemResult result) => result switch
    {
        RedeemResult.Missing => "An invite code is needed to join during testing.",
        RedeemResult.Unknown => "That invite code isn't one we know. Check it and try again.",
        RedeemResult.Revoked => "That invite code has been withdrawn.",
        RedeemResult.Expired => "That invite code has expired.",
        RedeemResult.UsedUp => "That invite code has already been used.",
        _ => string.Empty,
    };

    public async Task<IReadOnlyList<InviteCode>> MintAsync(int count, int maxUses, string? note, int? days)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var made = new List<InviteCode>();
        while (made.Count < count)
        {
            string code = NewCode();
            if (made.Any(c => c.Code == code) || await _context.InviteCodes.AnyAsync(c => c.Code == code)) continue;
            made.Add(new InviteCode
            {
                Code = code,
                Note = note,
                MaxUses = Math.Max(1, maxUses),
                CreatedAt = now,
                ExpiresAt = days is > 0 ? now.AddDays(days.Value) : null,
            });
        }
        _context.InviteCodes.AddRange(made);
        await _context.SaveChangesAsync();
        return made;
    }

    /// <summary>Spends one use of the code, if it has one to spend.</summary>
    public async Task<RedeemResult> RedeemAsync(string? rawCode)
    {
        string code = Normalize(rawCode);
        if (code.Length == 0) return RedeemResult.Missing;

        var invite = await _context.InviteCodes.FirstOrDefaultAsync(c => c.Code == code);
        if (invite == null) return RedeemResult.Unknown;
        if (invite.Revoked) return RedeemResult.Revoked;
        if (invite.ExpiresAt is { } expires && expires <= _clock.GetUtcNow().UtcDateTime) return RedeemResult.Expired;
        if (invite.Uses >= invite.MaxUses) return RedeemResult.UsedUp;

        invite.Uses++;
        try
        {
            await _context.SaveChangesAsync();
            return RedeemResult.Redeemed;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else spent a use at the same moment; the loser is told it is used up rather
            // than retried, which for a single-use code is the truth.
            await _context.Entry(invite).ReloadAsync();
            return RedeemResult.UsedUp;
        }
    }

    /// <summary>Gives back a use spent on a registration that then failed.</summary>
    public async Task RefundAsync(string? rawCode)
    {
        string code = Normalize(rawCode);
        var invite = await _context.InviteCodes.FirstOrDefaultAsync(c => c.Code == code);
        if (invite == null || invite.Uses == 0) return;
        invite.Uses--;
        await _context.SaveChangesAsync();
    }

    public async Task<bool> RevokeAsync(string rawCode)
    {
        string code = Normalize(rawCode);
        var invite = await _context.InviteCodes.FirstOrDefaultAsync(c => c.Code == code);
        if (invite == null) return false;
        invite.Revoked = true;
        await _context.SaveChangesAsync();
        return true;
    }

    private static string NewCode()
    {
        Span<char> chars = stackalloc char[9];
        for (int i = 0; i < 9; i++)
            chars[i] = i == 4 ? '-' : Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    // -----------------------------------------------------------------
    // Command line
    // -----------------------------------------------------------------

    public static async Task<int> RunFromCommandLineAsync(IServiceProvider services, string[] args)
    {
        string? Arg(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var invites = scope.ServiceProvider.GetRequiredService<InviteCodeService>();

        if (args.Contains("--list"))
        {
            var all = await context.InviteCodes.OrderBy(c => c.CreatedAt).ToListAsync();
            foreach (var c in all)
            {
                string state = c.Revoked ? "revoked" : c.ExpiresAt <= DateTime.UtcNow ? "expired" : $"{c.Uses}/{c.MaxUses} used";
                Console.WriteLine($"{c.Code}  {state,-12} {c.CreatedAt:yyyy-MM-dd}  {c.Note}");
            }
            Console.WriteLine($"{all.Count} code(s).");
            return 0;
        }

        if (Arg("--revoke") is { } revoke)
        {
            bool found = await invites.RevokeAsync(revoke);
            Console.WriteLine(found ? $"Revoked {Normalize(revoke)}." : $"No code {Normalize(revoke)}.");
            return found ? 0 : 1;
        }

        int count = int.TryParse(Arg("--count"), out var n) ? Math.Clamp(n, 1, 100) : 1;
        int uses = int.TryParse(Arg("--uses"), out var u) ? u : 1;
        int? days = int.TryParse(Arg("--days"), out var d) ? d : null;
        var made = await invites.MintAsync(count, uses, Arg("--note"), days);
        foreach (var c in made) Console.WriteLine(c.Code);
        Console.WriteLine($"Minted {made.Count} code(s), {Math.Max(1, uses)} use(s) each" +
                          (days is > 0 ? $", expiring in {days} day(s)." : ", no expiry."));
        return 0;
    }
}
