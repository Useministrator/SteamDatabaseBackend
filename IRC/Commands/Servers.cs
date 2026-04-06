/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Linq;
using System.Threading.Tasks;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal class ServersCommand : Command
    {
        private readonly SteamMasterServer MasterServer;

        public ServersCommand()
        {
            Trigger = "servers";
            IsSteamCommand = true;

            MasterServer = Steam.Instance.MasterServer;
        }

        public override async Task OnCommand(CommandArguments command)
        {
            if (command.Message.Length == 0)
            {
                command.Reply($"Usage:{Colors.OLIVE} servers <filter> - See https://developer.valvesoftware.com/wiki/Master_Server_Query_Protocol");

                return;
            }

            if (!command.Message.Contains('\\', StringComparison.Ordinal))
            {
                command.Reply("That doesn't look like a filter.");

                return;
            }

            var request = new SteamMasterServer.QueryDetails
            {
                Filter = command.Message,
                MaxServers = int.MaxValue,
                Region = ERegionCode.World,
            };

            var task = MasterServer.ServerQuery(request);
            task.Timeout = TimeSpan.FromSeconds(10);
            var servers = (await task).Servers;

            if (servers.Count == 0)
            {
                command.Reply("No servers.");

                return;
            }

            if (servers.Count == 1)
            {
                var server = servers[0];

                command.Reply($"{server.EndPoint} - {Colors.GREEN}{server.AuthedPlayers}{Colors.NORMAL} authenticated players");

                return;
            }

            command.Reply($"{Colors.GREEN}{servers.Sum(x => x.AuthedPlayers)}{Colors.NORMAL} authenticated players on {Colors.GREEN}{servers.Count}{Colors.NORMAL} servers. First three: {string.Join(" / ", servers.Take(3).Select(x => x.EndPoint))}");
        }
    }
}
