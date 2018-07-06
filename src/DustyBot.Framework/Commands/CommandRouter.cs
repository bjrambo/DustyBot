﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord.WebSocket;
using Discord;
using DustyBot.Framework.Utility;

namespace DustyBot.Framework.Commands
{
    class CommandRouter : Events.EventHandler, ICommandRouter
    {
        public IEnumerable<ICommandHandler> Handlers => _handlers;

        HashSet<ICommandHandler> _handlers = new HashSet<ICommandHandler>();
        Dictionary<string, List<CommandRegistration>> _commandsMapping = new Dictionary<string, List<CommandRegistration>>();

        public Communication.ICommunicator Communicator { get; set; }
        public Logging.ILogger Logger { get; set; }
        public Config.IEssentialConfig Config { get; set; }

        public CommandRouter(IEnumerable<ICommandHandler> handlers, Communication.ICommunicator communicator, Logging.ILogger logger, Config.IEssentialConfig config)
        {
            Communicator = communicator;
            Logger = logger;
            Config = config;

            handlers.ForEach(x => Register(x));
        }

        public void Register(ICommandHandler handler)
        {
            _handlers.Add(handler);

            foreach (var command in handler.HandledCommands)
            {
                List<CommandRegistration> commands;
                if (!_commandsMapping.TryGetValue(command.InvokeString.ToLowerInvariant(), out commands))
                    _commandsMapping.Add(command.InvokeString.ToLowerInvariant(), commands = new List<CommandRegistration>());

                commands.Add(command);
            }
        }

        public override async Task OnMessageReceived(SocketMessage message)
        {
            try
            {
                //Filter messages from bots
                if (message.Author.IsBot)
                    return;

                //Filter anything but normal user messages
                var userMessage = message as SocketUserMessage;
                if (userMessage == null)
                    return;

                //Try to find a command registration for this message
                CommandRegistration commandRegistration;
                if (!TryGetCommandRegistration(userMessage, out commandRegistration))
                    return;

                //Check if the channel type is valid for this command
                if (!IsValidCommandSource(message.Channel, commandRegistration))
                {
                    if (message.Channel is ITextChannel && commandRegistration.DirectMessageOnly)
                        await Communicator.CommandReplyDirectMessageOnly(message.Channel, commandRegistration);

                    return;
                }

                //Create command
                ICommand command;
                if (!SocketCommand.TryCreate(userMessage, Config, out command, commandRegistration.HasVerb))
                    return;
                
                //Log; don't log command content for non-guild channels, since these commands are usually meant to be private
                if (command.Guild != null)
                    await Logger.Log(new LogMessage(LogSeverity.Info, "Command", $"\"{message.Content}\" by {message.Author.Username} ({message.Author.Id}) on {command.Guild.Name}"));
                else
                    await Logger.Log(new LogMessage(LogSeverity.Info, "Command", $"\"{command.Invoker}{command.Verb?.PadLeft(command.Verb.Length + 1) ?? ""}\" by {message.Author.Username} ({message.Author.Id})"));

                //Check owner
                if (commandRegistration.OwnerOnly && !Config.OwnerIDs.Contains(message.Author.Id))
                {
                    await Communicator.CommandReplyNotOwner(command.Message.Channel, commandRegistration);
                    return;
                }

                //Check guild permisssions
                if (command.Guild != null)
                {
                    //User
                    var guildUser = message.Author as IGuildUser;
                    var missingPermissions = commandRegistration.RequiredPermissions.Where(x => !guildUser.GuildPermissions.Has(x));
                    if (missingPermissions.Count() > 0)
                    {
                        await Communicator.CommandReplyMissingPermissions(command.Message.Channel, commandRegistration, missingPermissions);
                        return;
                    }

                    //Bot
                    var selfUser = await command.Guild.GetCurrentUserAsync();
                    var missingBotPermissions = commandRegistration.BotPermissions.Where(x => !selfUser.GuildPermissions.Has(x));
                    if (missingBotPermissions.Count() > 0)
                    {
                        await Communicator.CommandReplyMissingBotPermissions(command.Message.Channel, commandRegistration, missingBotPermissions);
                        return;
                    }
                }

                //Check expected parameters
                if (!CheckRequiredParameters(command, commandRegistration))
                {
                    await Communicator.CommandReplyIncorrectParameters(command.Message.Channel, commandRegistration, "");
                    return;
                }

                //Execute
                var executor = new Func<Task>(async () =>
                {
                    try
                    {
                        await commandRegistration.Handler(command);
                    }
                    catch (Exceptions.IncorrectParametersCommandException ex)
                    {
                        await Communicator.CommandReplyIncorrectParameters(command.Message.Channel, commandRegistration, ex.Message);
                    }
                    catch (Exceptions.MissingBotPermissionsException ex)
                    {
                        await Communicator.CommandReplyMissingBotPermissions(command.Message.Channel, commandRegistration, ex.Permissions);
                    }
                    catch (Exceptions.MissingBotChannelPermissionsException ex)
                    {
                        await Communicator.CommandReplyMissingBotPermissions(command.Message.Channel, commandRegistration, ex.Permissions);
                    }
                    catch (Exception ex)
                    {
                        await Logger.Log(new LogMessage(LogSeverity.Error, "Internal",
                            $"Exception encountered while processing command {commandRegistration.InvokeString} in module {commandRegistration.Handler.Target.GetType()}", ex));

                        await Communicator.CommandReplyGenericFailure(command.Message.Channel, commandRegistration);
                    }
                });
                
                if (commandRegistration.RunAsync)
                    TaskHelper.FireForget(executor);
                else
                    await executor();
            }
            catch (Exception ex)
            {
                await Logger.Log(new LogMessage(LogSeverity.Error, "Internal", "Failed to process potential command message.", ex));
            }
        }

        private bool TryGetCommandRegistration(SocketUserMessage userMessage, out CommandRegistration commandRegistration)
        {
            commandRegistration = null;

            //Check if the message contains a command invoker
            var invoker = SocketCommand.ParseInvoker(userMessage, Config.CommandPrefix);
            if (string.IsNullOrEmpty(invoker))
                return false;

            //Try to find command registrations for this invoker
            List<CommandRegistration> commandRegistrations;
            if (!_commandsMapping.TryGetValue(invoker.ToLowerInvariant(), out commandRegistrations))
                return false;

            //If there's a potential verb, try to find a registration of this invoker and verb combination
            string verb = SocketCommand.ParseVerb(userMessage);
            if (!string.IsNullOrEmpty(verb))
                commandRegistration = commandRegistrations.FirstOrDefault(x => x.HasVerb && string.Compare(x.Verb, verb, true) == 0);

            //Or try to find a command registration for this invoker without a verb
            if (commandRegistration == null)
                commandRegistration = commandRegistrations.FirstOrDefault(x => !x.HasVerb);

            return commandRegistration != null;
        }

        private bool IsValidCommandSource(IMessageChannel channel, CommandRegistration cr)
        {
            if (channel is ITextChannel && !cr.DirectMessageOnly)
                return true;

            if (channel is IDMChannel && cr.DirectMessageAllow)
                return true;

            return false;
        }

        private bool CheckRequiredParameters(ICommand command, CommandRegistration commandRegistration)
        {
            if (command.ParametersCount < commandRegistration.RequiredParameters.Count)
                return false;

            for (int i = 0; i < commandRegistration.RequiredParameters.Count; ++i)
            {
                if (!command.GetParameter(i).IsType(commandRegistration.RequiredParameters[i]))
                    return false;
            }

            return true;
        }
    }
}
