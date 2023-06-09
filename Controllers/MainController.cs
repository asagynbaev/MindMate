﻿using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MindMate.Entities;
using OpenAI_API;
using OpenAI_API.Chat;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace MindMate.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class MainController : ControllerBase
    {
        private readonly ILogger<MainController> _logger;
        private readonly DialogContext _context;
        private static readonly ConcurrentDictionary<long, Conversation> _userConversations = new ConcurrentDictionary<long, Conversation>();
        private readonly OpenAIAPI _openAIAPI;
        private readonly TelegramBotClient _telegramBotClient;

        public MainController(ILogger<MainController> logger, DialogContext context)
        {
            _logger = logger;
            _context = context;
            _openAIAPI = new OpenAIAPI(new APIAuthentication(DotNetEnv.Env.GetString("OPEN_AI_API")));
            _telegramBotClient = TelegramBot.GetTelegramBot();
        }

        private TelegramBotClient bot = TelegramBot.GetTelegramBot();

        [HttpPost("talk")]
        public async Task Post([FromBody] Update update) //Update receiver method
        {
            try
            { 
                // Getting user's telegram user id
                long chatId = update.Message.Chat.Id;
                
                if (update.Message.Text != null)
                {
                    // Check if this is a first interaction
                    if (update.Message.Text == "/start")
                    {
                        // using Telegram.Bot.Types.ReplyMarkups;
                        ReplyKeyboardMarkup replyKeyboardMarkup = new(new[]
                        {
                            new KeyboardButton[] { "English", "Русский" },
                        })
                        { ResizeKeyboard = true, OneTimeKeyboard = true };

                        await TelegramBot.DoConversation(update.Message.Chat.Id, replyKeyboardMarkup, "Выберите язык общения. Choose your language.");
                    }
                    else
                    {
                        // Check if there is a conversation, if not to create
                        var conversation = GetOrCreateConversation(chatId);
                        ReplyKeyboardRemove replyKeyboardRemove = new ReplyKeyboardRemove();

                        if(update.Message.Text == "Русский")
                        {
                            conversation.AppendSystemMessage(DotNetEnv.Env.GetString("BOT_CONTEXT_SETTINGS_RU"));
                            await bot.SendTextMessageAsync(chatId, DotNetEnv.Env.GetString("HELLO_MESSAGE_RU"), replyMarkup: replyKeyboardRemove);
                            await SavePatient(update.Message.Chat.Id, update.Message.Chat.Username, update.Message.Chat.FirstName, update.Message.Chat.LastName, "ru");
                        }
                        else if(update.Message.Text == "English")
                        {
                            conversation.AppendSystemMessage(DotNetEnv.Env.GetString("BOT_CONTEXT_SETTINGS_EN"));
                            await bot.SendTextMessageAsync(chatId, DotNetEnv.Env.GetString("HELLO_MESSAGE_EN"), replyMarkup: replyKeyboardRemove);
                            await SavePatient(update.Message.Chat.Id, update.Message.Chat.Username, update.Message.Chat.FirstName, update.Message.Chat.LastName, "en");
                        }
                        else
                        {
                            Patient lang = await _context.Patients.SingleOrDefaultAsync(x => x.TelegramUserId == update.Message.Chat.Id);
                            string newresult = await TelegramBot.DoConversation(chatId, conversation, update.Message.Text, lang.Language);

                            var dialog = new Dialog
                            {
                                Username = update.Message.Chat.Username,
                                UserMessage = update.Message.Text,
                                BotResponse =  newresult,
                                Timestamp = DateTime.UtcNow,
                                TelegramUserId = update.Message.Chat.Id.ToString()
                            };

                            _context.Dialogs.Add(dialog);
                            _context.SaveChanges();
                            }
                    }
                }
            }
            catch(Exception ex)
            {
                _logger.LogError(ex.Message);
                _context.Errors.Add(new ErrorLogs(ex.Message, ex.InnerException.Message, "talk"));
                await _context.SaveChangesAsync();
            }
        }
        
        [HttpGet("send-notification/{text}")]
        public async Task SendReminderMessage(string text)
        {
            try
            {
                List<Patient> patients = await _context.Patients.ToListAsync();
                var message = text;
                foreach(var item in patients)
                {
                    if(item.TelegramUserId != 0)
                    {
                        await TelegramBot.DoConversation(item.TelegramUserId, message);
                    }
                }
            }
            catch(Exception ex)
            {
                _logger.LogError(ex.Message);
                _context.Errors.Add(new ErrorLogs(ex.Message, ex.InnerException.Message, "talk"));
                await _context.SaveChangesAsync();
            }
        }

        [HttpGet("send-message-to-users-who-didnt-use/{text}")]
        public async Task SendFirst(string text)
        {
            long tuserid = 0;
            try
            {
                List<Patient> patients = await _context.Patients.ToListAsync();
                List<Dialog> dialogs = await _context.Dialogs.ToListAsync();

                // Retrieve patients that do not exist in the Dialog table
                IEnumerable<Patient> patientsNotInDialog = patients.Where(p => !dialogs.Any(d => d.TelegramUserId == p.TelegramUserId.ToString() && d.TelegramUserId != null));

                var message = text;
                foreach(var item in patients)
                {
                    if(item.TelegramUserId != 0 && !item.BlockedByUser)
                    {
                        tuserid = item.TelegramUserId;
                        await TelegramBot.DoConversation(item.TelegramUserId, message);
                    }
                }
            }
            catch(Exception ex)
            {
                if(ex.Message == "Forbidden: bot was blocked by the user")
                {
                    var patient = await _context.Patients.SingleOrDefaultAsync(x => x.TelegramUserId == tuserid);
                    patient.BlockedByUser = true;
                    await _context.SaveChangesAsync();
                }
                _logger.LogError(ex.Message);
                var error = new ErrorLogs(ex.Message, ex.InnerException.Message, "send-message-to-users-who-didnt-use");
                _context.Errors.Add(error);
                await _context.SaveChangesAsync();
            }
        }

        private Conversation GetOrCreateConversation(long chatId)
        {
            if (_userConversations.TryGetValue(chatId, out var conversation))
            {
                return conversation;
            }
            else
            {
                conversation = _openAIAPI.Chat.CreateConversation();
                _userConversations.TryAdd(chatId, conversation);
                return conversation;
            }
        }

        private async Task SavePatient(long patientId, string? username, string? firstname, string? lastname, string language)
        {
            Patient ifExists = await _context.Patients.SingleOrDefaultAsync(x => x.TelegramUserId == patientId);

            // Check if user exists, if not, put it in database
            if(ifExists == null)
            {
                Patient patient = new Patient()
                {
                    Username = username,
                    TelegramUserId = patientId,
                    Firstname = firstname,
                    Lastname = lastname,
                    CreatedAt = DateTime.UtcNow,
                    Language = language
                };

                _context.Patients.Add(patient);
                await _context.SaveChangesAsync();
                _logger.LogInformation($"Saved: {patient.Id}, {patient.Username}");
            }
        }
    }
}
