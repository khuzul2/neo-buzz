// Portable build of the native machine host (docs/specs/cross-platform.md R1): ipc/ServerProcess.h's child side
// on POSIX. The protocol is the same, bit for bit (the shared memory layout of ipc/ServerSharedMem.h, the states
// of ipc/Message.h's channels). Only the transport changes: the two named events of each channel ("Ping" and
// "Pong") are two FIFOs, one byte per Set, and the shared memory is a file the parent maps (NativeHostLink.cs).
#pragma once

#include <functional>
#include <string>
#include <vector>
#include <list>
#include <map>
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>
#include "ServerSharedMem.h"
#include "../../../ipc/Message.h"

namespace IPC
{

// An auto-reset event: Set writes a token, Wait blocks for one. As on Windows, the protocol alternates strictly,
// so tokens never pile up.
class Event
{
public:
	bool Open(const std::string& path)
	{
		fd = ::open(path.c_str(), O_RDWR);
		return fd >= 0;
	}
	void Set()
	{
		char c = 1;
		while (::write(fd, &c, 1) < 0 && errno == EINTR) {}
	}
	bool Wait()
	{
		char c;
		for (;;)
		{
			ssize_t n = ::read(fd, &c, 1);
			if (n == 1) return true;
			if (n < 0 && errno == EINTR) continue;
			return false;
		}
	}
private:
	int fd = -1;
};

class Channel
{
public:
	typedef std::function<void (Message const &, Message &)> ReceiveCallback;

	// dir: the session folder (the parent made the FIFOs there); name: "Host", "UI", "MIDI" or "Audio".
	bool InitChild(SharedMem *pSharedMem, int index, const std::string& dir, const char *name)
	{
		this->pSharedMem = pSharedMem;
		channel = index;
		callbackReceiveCallback = NULL;
		inReceiveCallback = false;
		return ping.Open(dir + "/" + name + ".ping") && pong.Open(dir + "/" + name + ".pong");
	}

	void DoCall()
	{
		ping.Set();
		pong.Wait();
	}

	void Listen(ReceiveCallback receiveCb)
	{
		this->receiveCallback = receiveCb;
		while (ping.Wait())
			HandleMessage();
	}

	void HandleMessage()
	{
		MessageBuffer &mb = pSharedMem->channels[channel].msgBuffer;

		switch(pSharedMem->channels[channel].state)
		{
		case ChannelSharedMem::sendbuffer:
			recvMsg.Write(mb.data, mb.size);		// Append message from shared memory. There is going to be another message after this
			mb.Reset();
			pong.Set();								// Send response
			break;
		case ChannelSharedMem::sendlastbuffer:
			{
				recvMsg.Write(mb.data, mb.size);	// Append message from shared memory.
				mb.Reset();

				Message reply;
				inReceiveCallback = true;
				receiveCallback(recvMsg, reply);	// Process received message and get reply
				inReceiveCallback = false;
				Reply(reply);

				recvMsg.Reset();
			}
			break;
		default:
			break;
		}
	}

	void Send(Message const &msg, Message &reply)
	{
		pReply = &reply;

		MessageBuffer &mb = pSharedMem->channels[channel].msgBuffer;

		mb.Reset();
		mb.Write(msg.GetData(), msg.GetSize(), this, &IPC::Channel::SendBufferCallback);
	}

	void Reply(Message const &msg)
	{
		MessageBuffer &mb = pSharedMem->channels[channel].msgBuffer;

		mb.Reset();
		mb.Write(msg.GetData(), msg.GetSize(), this, &Channel::ReplyBufferCallback);
	}

	void Callback(Message const &msg, Message &reply)
	{
		pCallbackReply = &reply;
		pSharedMem->channels[channel].callbackMode = true;

		MessageBuffer &mb = pSharedMem->channels[channel].msgBuffer;

		mb.Reset();
		mb.Write(msg.GetData(), msg.GetSize(), this, &Channel::CallbackCallback);
	}

	bool IsInReceiveCallback() const { return inReceiveCallback; }

private:
	static void SendBufferCallback(Channel *pchn, bool done)
	{
		pchn->SendBufferCallbackImpl(done);
	}

	void SendBufferCallbackImpl(bool done)
	{
		pSharedMem->channels[channel].state = done ? ChannelSharedMem::sendlastbuffer : ChannelSharedMem::sendbuffer;
		DoCall();

		MessageBuffer &mb = pSharedMem->channels[channel].msgBuffer;

		if (done)
		{
			pReply->Reset();

			while(true)
			{
				pReply->Write(mb.data, mb.size);
				mb.Reset();

				if (pSharedMem->channels[channel].state == ChannelSharedMem::replylastbuffer)
				{
					if (pSharedMem->channels[channel].callbackMode)
					{
						Message callbackReply;
						callbackReceiveCallback(*pReply, callbackReply);
						mb.Write(callbackReply.GetData(), callbackReply.GetSize(), this, &Channel::SendBufferCallback);
					}

					break;
				}

				DoCall();
			}
		}
	}

	static void ReplyBufferCallback(Channel *pchn, bool done)
	{
		pchn->ReplyBufferCallbackImpl(done);
	}

	void ReplyBufferCallbackImpl(bool done)
	{
		pSharedMem->channels[channel].state = done ? ChannelSharedMem::replylastbuffer : ChannelSharedMem::replybuffer;
		if (done)
		{
			pong.Set();		// Reply sent
		}
		else
		{
			pong.Set();		// Whole reply is not sent
			ping.Wait();
		}
	}

	static void CallbackCallback(Channel *pchn, bool done)
	{
		pchn->CallbackCallbackImpl(done);
	}

	void CallbackCallbackImpl(bool done)
	{
		pSharedMem->channels[channel].state = done ? ChannelSharedMem::replylastbuffer : ChannelSharedMem::replybuffer;
		pong.Set();
		ping.Wait();

		MessageBuffer &mb = pSharedMem->channels[channel].msgBuffer;

		if (done)
		{
			pCallbackReply->Reset();

			while(true)
			{
				pCallbackReply->Write(mb.data, mb.size);
				mb.Reset();

				if (pSharedMem->channels[channel].state == ChannelSharedMem::sendlastbuffer)
				{
					pSharedMem->channels[channel].callbackMode = false;
					break;
				}

				pong.Set();
				ping.Wait();
			}
		}
	}

	Event ping;
	Event pong;

	SharedMem *pSharedMem = NULL;
	int channel = 0;

	Message recvMsg;
	Message *pReply = NULL;
	Message *pCallbackReply = NULL;

	ReceiveCallback receiveCallback;
	ReceiveCallback callbackReceiveCallback;

	bool inReceiveCallback = false;
};

}
