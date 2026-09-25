using System.Diagnostics.CodeAnalysis;
using MuggaLuggaTD.Shared.Gameplay;

namespace MuggaLuggaTD_2D.API.Services;

/// <summary>
/// The two kinds of ally template. One that ships with a signature roll is a <b>starter</b>: every new
/// player is given one of each, and a save carrying exactly that roll needs no hire record. One
/// without a roll is only a <b>face</b> for the Tavern - the server rolls whoever it puts on one.
///
/// <para>Most templates are faces (the generated sheets in tools/lpc), so anything that counts or
/// recognises the starting roster has to ask this rather than count every template.</para>
/// </summary>
public static class RecruitSheetKinds
{
    public static bool IsStarter([NotNullWhen(true)] this RecruitSheet? sheet) =>
        sheet != null && !string.IsNullOrEmpty(sheet.SignatureId) && sheet.SignatureAffinity != null;
}
