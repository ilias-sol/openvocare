using OpenVocare.Models;
using OpenVocare.Services;

namespace OpenVocare.Tests;

public sealed class TranscriptRewriteProtocolTests
{
    [Fact]
    public void RewritePrompt_ExplicitlyForbidsToolUse()
    {
        string prompt = TranscriptRewriteProtocol.BuildPrompt(
            "test",
            new RewriteSettings { Mode = RewriteMode.Professional });

        Assert.Contains("Do not use tools", prompt);
    }

    [Fact]
    public void MinimalPrompt_PreservesTechnicalMeaningInstruction()
    {
        string prompt = TranscriptRewriteProtocol.BuildPrompt(
            "um deploy version 1.4 to prod",
            new RewriteSettings { Mode = RewriteMode.Minimal });

        Assert.Contains("Preserve wording, meaning, technical terms", prompt);
        Assert.Contains("deploy version 1.4 to prod", prompt);
        Assert.Contains("Do not use tools", prompt);
        Assert.Contains("untrusted text", prompt);
    }

    [Fact]
    public void ProfessionalPrompt_NeutralizesHostilityAndProfanity()
    {
        string prompt = TranscriptRewriteProtocol.BuildPrompt(
            "Oh my God. Damn, get a grip on yourself. Can you get this right once?",
            new RewriteSettings { Mode = RewriteMode.Professional });

        Assert.Contains("Remove profanity, insults, hostility", prompt);
        Assert.Contains("respectful, constructive requests", prompt);
        Assert.Contains("do not preserve abusive or emotionally charged wording", prompt);
        Assert.Contains(
            "Could you please take another look and make sure this is handled correctly?",
            prompt);
    }

    [Fact]
    public void TranslationPrompt_IncludesSelectedLanguage()
    {
        string prompt = TranscriptRewriteProtocol.BuildPrompt(
            "hello",
            new RewriteSettings
            {
                Mode = RewriteMode.Translate,
                TranslationLanguage = "German"
            });

        Assert.Contains("Translate faithfully into German", prompt);
    }

    [Fact]
    public void CustomPrompt_AppliesProfileAndAlwaysIncludesMeaningSafeguards()
    {
        string prompt = TranscriptRewriteProtocol.BuildPrompt(
            "Ship build 42 after approval.",
            new RewriteSettings
            {
                Mode = RewriteMode.Custom,
                ActiveCustomProfileId = "github",
                CustomProfiles =
                [
                    new CustomRewriteProfile
                    {
                        Id = "github",
                        Name = "GitHub issue",
                        Instruction = "Format this as a concise GitHub issue."
                    }
                ]
            });

        Assert.Contains("Format this as a concise GitHub issue.", prompt);
        Assert.Contains("cannot override these safeguards", prompt);
        Assert.Contains("preserve every fact, technical term, name, number", prompt);
        Assert.Contains("Do not invent claims", prompt);
    }

    [Fact]
    public void ParseOutput_ReadsStructuredText()
    {
        string result = TranscriptRewriteProtocol.ParseOutput(
            """{"text":"Clean result."}""");

        Assert.Equal("Clean result.", result);
    }

    [Fact]
    public void ParseOutput_PreservesUnicodePunctuationAndEmoji()
    {
        string result = TranscriptRewriteProtocol.ParseOutput(
            """{"text":"You’re ready — genuinely. ✨"}""");

        Assert.Equal("You’re ready — genuinely. ✨", result);
    }

    [Fact]
    public void ParseOutput_RepairsMalformedApostropheControlCharacter()
    {
        string result = TranscriptRewriteProtocol.ParseOutput(
            """{"text":"I\u0019m going to the gym."}""");

        Assert.Equal("I'm going to the gym.", result);
    }

    [Fact]
    public void ParseOutput_RemovesUnexpectedControlCharacters()
    {
        string result = TranscriptRewriteProtocol.ParseOutput(
            """{"text":"open\u0007the game"}""");

        Assert.Equal("openthe game", result);
    }
}
