namespace Fika.Core.Main.Utils;

/// <summary>
/// The 「值未变就不写」 guards of the observed-player pose writes (config 'Coop / Pose Write Guards').
///
/// The native setters of this game push their value into the animator unconditionally
/// (MovementContext.SmoothedCharacterMovementSpeed and SprintSpeed both call into PlayerAnimator), and this
/// port used to write them for every observed player on every frame even when the value had not changed -
/// which is what the 「下压再回正」 stutter shape was traced to (analysis/stutter-pose-reset).
///
/// False restores the pre-guard behaviour exactly: every site writes on every frame again. Each site pays
/// one extra static bool read (about 500 reads per frame at 61 observed players).
/// </summary>
internal static class PoseWriteGuards
{
    internal static bool Enabled = true;
}
