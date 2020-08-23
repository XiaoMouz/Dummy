﻿using Cysharp.Threading.Tasks;
using EvolutionPlugins.Dummy.API;
using EvolutionPlugins.Dummy.Patches;
using Microsoft.Extensions.Logging;
using OpenMod.API.Plugins;
using OpenMod.Unturned.Plugins;
using System;
using ILogger = Microsoft.Extensions.Logging.ILogger;

[assembly: PluginMetadata("EvolutionPlugins.Dummy", Author = "DiFFoZ", DisplayName = "Dummy")]

namespace EvolutionPlugins.Dummy
{
    public class Dummy : OpenModUnturnedPlugin
    {
        private readonly ILogger m_Logger;
        private readonly IDummyProvider m_DummyProvider;

        public Dummy(IServiceProvider serviceProvider, ILogger<Dummy> logger, IDummyProvider dummyProvider) : base(serviceProvider)
        {
            m_Logger = logger;
            m_DummyProvider = dummyProvider;
        }

        protected override UniTask OnLoadAsync()
        {
            Patch_Provider_receiveServer.onNeedProvider += GiveProvider;
            Patch_Provider_send.OnNeedProvider += GiveProvider;
            Patch_Provider_verifyNextPlayerInQueue.OnNeedProvider += GiveProvider;

            m_Logger.LogInformation("Made with <3 by Evolution Plugins");
            m_Logger.LogInformation("https://github.com/evolutionplugins \\ https://github.com/diffoz");
            m_Logger.LogInformation("Discord: DiFFoZ#6745");
            return UniTask.CompletedTask;
        }

        protected override UniTask OnUnloadAsync()
        {
            Patch_Provider_receiveServer.onNeedProvider -= GiveProvider;
            Patch_Provider_send.OnNeedProvider -= GiveProvider;
            Patch_Provider_verifyNextPlayerInQueue.OnNeedProvider -= GiveProvider;

            return UniTask.CompletedTask;
        }

        private IDummyProvider GiveProvider()
        {
            return m_DummyProvider;
        }
    }
}
