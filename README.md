# Ragnarok Sync

haha get it it's funny because the ragnarok is an interplanetary vessel and im trying to use IPFS to distribute the mod files

This is (as far as I'm aware, may be bugs in edge cases) a working demo of how one might coordinate IPFS to avoid having to handle the kind of traffic Mare had to in its prime.

I'm aware of the risk of IP leaks, but I think you should be mostly obfuscated in a network of decent size assuming you don't have an absolutely unique mod and are, like most users, made up of mods that are publicly downloaded. Stick your kubo behind a VPN if it's a major concern

In light of Square's announcement on mod-sharing tools I'm probably not going to take this very far or host a server (I really don't want to deal with the software maintenance anyway), so consider this an educational resource. I think the 'future' of mod sharing may have to be something similar in nature to PeerTube, they seem to be the same fundamental problem

## How to setup

Install IPFS desktop (or just a kubo node if you're fancy)

Build the plugin and add it as a dev plugin (or publish it to a repo somewhere)

Set up the server in [the server repo](https://github.com/eqbot/RagnarokServer) and add a custom server config to connect to it

Use as normal. Smile as your server doesn't have to transfer terabytes of unoptimized mod textures.

