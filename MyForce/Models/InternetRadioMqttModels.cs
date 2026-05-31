using System;

namespace MyForce.Models;

/// <summary>
/// Defines the MQTT topics used by the UI and AP for internet radio control.
/// </summary>
internal static class InternetRadioMqttTopics
{
	public const string PlayCommandTopic = "myforce/ap/cmd/internet-radio/play";
	public const string StopCommandTopic = "myforce/ap/cmd/internet-radio/stop";
	public const string StateTopic = "myforce/ap/state/internet-radio";
}

/// <summary>
/// Represents a UI request for the AP to start internet radio playback.
/// </summary>
internal sealed record InternetRadioPlayCommandMessage(
	string StreamUrl,
	string DisplayName,
	string Genre,
	string Language);

/// <summary>
/// Represents the retained AP internet radio playback state sent over MQTT.
/// </summary>
internal sealed record InternetRadioPlaybackStateMessage(
	bool IsPlaying,
	string? StreamUrl,
	string? DisplayName,
	string? Genre,
	string? Language,
	string Status,
	string Detail);

/// <summary>
/// Represents a UI request to update an AP mixer channel gain.
/// </summary>
internal sealed record AudioChannelGainCommandMessage(
	string ChannelId,
	decimal Gain);
