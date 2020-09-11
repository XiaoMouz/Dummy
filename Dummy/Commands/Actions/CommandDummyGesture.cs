﻿using EvolutionPlugins.Dummy.API;
using EvolutionPlugins.Dummy.Extensions.Interaction.Actions;
using OpenMod.API.Commands;
using OpenMod.Core.Commands;
using SDG.Unturned;
using Steamworks;
using System;
using System.Threading.Tasks;
using Command = OpenMod.Core.Commands.Command;

namespace EvolutionPlugins.Dummy.Commands.Actions
{
    [Command("gesture")]
    [CommandDescription("Make a dummy gesture")]
    [CommandParent(typeof(CommandDummy))]
    [CommandSyntax("<id> <gesture>")]
    public class CommandDummyGesture : Command
    {
        private readonly IDummyProvider m_DummyProvider;

        public CommandDummyGesture(IServiceProvider serviceProvider, IDummyProvider dummyProvider) : base(serviceProvider)
        {
            m_DummyProvider = dummyProvider;
        }

        protected override async Task OnExecuteAsync()
        {
            if (Context.Parameters.Count == 0)
            {
                throw new CommandWrongUsageException(Context);
            }
            var id = (CSteamID)await Context.Parameters.GetAsync<ulong>(0);

            var dummy = await m_DummyProvider.GetPlayerDummy(id.m_SteamID);
            if (dummy == null)
            {
                throw new UserFriendlyException($"Dummy \"{id}\" has not found!");
            }

            var gesture = await Context.Parameters.GetAsync<string>(1);
            if (!Enum.TryParse<EPlayerGesture>(gesture.ToUpper(), out var eGesture))
            {
                await PrintAsync($"Unable find a gesture {gesture}");
                await PrintAsync($"All gestures: {string.Join(",", Enum.GetNames(typeof(EPlayerGesture)))}");
                return;
            }
            dummy.Actions.Actions.Enqueue(new GestureAction(eGesture));
        }
    }
}