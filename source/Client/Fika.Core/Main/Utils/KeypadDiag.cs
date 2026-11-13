using System;
using System.Collections.Generic;

namespace Fika.Core.Main.Utils;

/// <summary>
/// BB (analysis/icebreaker-keypad-door): one grep-able line per passcode-door decision, written by the
/// machine that makes the decision. The family answers the questions the icebreaker report cannot be
/// judged from a client-only log without:
///
///   stage=menu        did the native interaction dispatcher offer an action for this PasscodeTerminal
///                     (the names are the native, localized ones - this is the "which interaction was
///                     eaten" line);
///   stage=panel       did the native PasscodeInputPanel open, and with which controller;
///   stage=submit      did the native submit pass the game's own guard, or was it refused with
///                     "Tried to enter code on location without passcodes" (and relayed to the host);
///   stage=init        did the host's passcode snapshot arrive here, and were its hints applied;
///   stage=attempt     (host) what happened to a peer's attempt, and whether the peer got an answer;
///   stage=result      (client) the host's answer reached this machine's panel;
///   stage=controller  which controller this client's world holds and whether the location carries
///                     passcode entries at all. BO (analysis/icebreaker-keypad-2) extended this stage with the
///                     three states of a client world that has NO controller: waiting (inside the print-free
///                     grace window), client-created (this port built the network controller itself, out of the
///                     ids of its own location scene), and kept-native (even that failed - the menu stays empty).
///                     'waited=' in every line of the family is the age of the raid when the decision was made.
///                     BU (analysis/icebreaker-keypad-3) added the host's own half of it (result=host-created).
///   stage=codes       BU: the raid's code table. One line says how the HOST got it (result=kept-native from
///                     the game's own controller, result=generated from the game's own PasscodeGenerator, or
///                     result=no-entries/no-codes when the location of that machine carries nothing to draw
///                     from), one says that a CLIENT received it (result=received/waiting) and one says that the
///                     client built the game's own local controller around it (result=local-native). With that
///                     line present, 'settings=' and 'terminals=' of the submit stage are the numbers to read.
///   stage=keys        BU: the keypads and hint groups of this machine's scene were bound to its controller
///                     (terminals=N hintGroups=M). A client's scene loads after its world, so a zero here at
///                     raid start is a timing fact and not a verdict - the binding is retried.
///
/// Every line is written once per distinct text and the family stops after <see cref="MaxLines"/> lines
/// per raid, so a whole raid costs a handful of dictionary hits and one grep for KeypadDiag in
/// LogOutput.log is the evidence. Nothing here ever throws into the native path it is called from.
/// </summary>
internal static class KeypadDiag
{
    /// <summary>
    /// Master switch of the keypad repair (analysis/icebreaker-keypad-door). With it off, neither the
    /// host relay of a submit the game refused nor the retry of the passcode snapshot request runs, and
    /// the port behaves exactly as it did before this round. It lives here - and not in FikaConfig.cs,
    /// which another agent owns - until the main agent wires it to a config entry of its own.
    /// </summary>
    internal static bool RepairEnabled = true;

    /// <summary>The diagnostic family itself. The lines are rare, so it is on by default.</summary>
    internal static bool Enabled = true;

    private const int MaxLines = 400;

    private static readonly HashSet<string> Written = new(StringComparer.Ordinal);

    /// <summary>host / client / headless / single - the vocabulary every line of this port uses.</summary>
    internal static string Side => FikaRaidGate.Side;

    /// <summary>"yes" while this machine is outside a Fika raid and therefore stays out of the native flow.</summary>
    internal static string Gated => FikaRaidGate.Inert ? "yes" : "no";

    internal static void Line(string stage, string detail)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            var text = "stage=" + stage + " side=" + Side + " gated=" + Gated + " " + detail;
            lock (Written)
            {
                if (Written.Count >= MaxLines || !Written.Add(text))
                {
                    return;
                }
            }

            FikaGlobals.LogInfo(text, "KeypadDiag");
        }
        catch (Exception)
        {
            // A diagnostic must never be the reason a native interaction fails.
        }
    }

    internal static void Reset()
    {
        lock (Written)
        {
            Written.Clear();
        }
    }
}
