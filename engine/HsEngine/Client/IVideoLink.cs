using Handshake.Engine.Net;

namespace Handshake.Engine.Client;

/// <summary>
/// Where the decoded stream comes from: the PC host over one or more paths (<see cref="PathManager"/>),
/// or an iPhone pushing its own screen to us (<see cref="MirrorLink"/>).
/// </summary>
public interface IVideoLink : IDisposable
{
    event Action<MsgType, ReadOnlyMemory<byte>>? Message;
    void SendActive(byte[] message);
    bool ActiveIsLocal { get; }   // sender shares our QPC clock
    long ClockOffsetUs { get; }   // sender clock minus our clock
    long RttUs { get; }
    int Failovers { get; }        // path switches / sender reconnects
}
