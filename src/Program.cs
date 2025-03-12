using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.ComponentModel;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using UglyToad.PdfPig;

var builder = WebApplication.CreateBuilder(args);

// Environment variables
var endpoint = Environment.GetEnvironmentVariable("OPENAI_ENDPOINT", EnvironmentVariableTarget.User);
var apikey = Environment.GetEnvironmentVariable("OPENAI_APIKEY", EnvironmentVariableTarget.User);
var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT", EnvironmentVariableTarget.User);
var deploymentName = "gpt-4o-mini";

// Add services to the container.
var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseHttpsRedirection();

// Bot settings
var bot = new TelegramBotClient(token!, cancellationToken: new CancellationTokenSource().Token);
var me = await bot.GetMe();

// Kernel settings
var kernelBuilder = Kernel.CreateBuilder();
var chatService = new AzureOpenAIChatCompletionService(deploymentName, endpoint!, apikey!);

kernelBuilder.Plugins.AddFromType<HospitalPlugin>();
kernelBuilder.Plugins.AddFromType<TelegramPlugin>();

var promptSettings = new AzureOpenAIPromptExecutionSettings()
{
    ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions
};

// Build kernel
var kernel = kernelBuilder.Build();

// System prompt
var systemPrompt = @"Eres un asistente médico experto. Solo debes responder a preguntas relacionadas con la salud, medicina y bienestar. Tienes la capacidad de analizar imágenes y archivos que se te envíen, proporcionando una descripción y posibles interpretaciones sobre su contenido médico. Puedes ofrecer recomendaciones generales sobre el manejo de síntomas comunes, como la fiebre, pero siempre enfatizando que es importante consultar a un profesional médico para una evaluación específica. Si una pregunta no está relacionada con estos temas, responde educadamente indicándolo. Sin embargo, si la pregunta está relacionada con algo que ya te ha contado el paciente o está en el historial de conversación, puedes dar una respuesta adecuada. Al analizar imágenes como radiografías o tomografías, aclara qué podrías inferir sobre la condición del paciente basándote en esa imagen.";

var chatHistoryDictionary = new Dictionary<long, ChatHistory>();

// Link events
bot.OnMessage += OnMessage;

// Run app
app.Run();

async Task OnMessage(Message msg, UpdateType type)
{
    // Get chat history
    var isNewChat = chatHistoryDictionary.TryGetValue(msg.Chat.Id, out var history);
    if (!isNewChat) chatHistoryDictionary[msg.Chat.Id] = new ChatHistory(systemPrompt);

    if (msg.Type == MessageType.Text)
    {
        if (msg.Text.StartsWith("/"))
        {
            await HandleOptionMessages(bot, msg, msg.Text);
        }
        else
        {
            chatHistoryDictionary[msg.Chat.Id].AddUserMessage(msg.Text);

            var answer = await chatService.GetChatMessageContentAsync(chatHistoryDictionary[msg.Chat.Id], promptSettings, kernel);
            await bot.SendMessage(msg.Chat, answer.ToString(), ParseMode.Markdown);
        }
    }

    if (msg.Type == MessageType.Document || msg.Type == MessageType.Photo)
    {
        // Get file stuff
        var fileId = msg.Type == MessageType.Document ? msg.Document.FileId : msg.Photo.Last().FileId;
        var fileInfo = await bot.GetFile(fileId);
        var filePath = fileInfo.FilePath;

        // Random path
        var fullNewPath = Path.Combine(Directory.GetCurrentDirectory(), Guid.NewGuid().ToString());

        // Download file
        await using (var fileStream = File.Create(fullNewPath))
        {
            await bot.DownloadFile(filePath, fileStream);
        }

        // Read bytes and delete file
        var bytes = await File.ReadAllBytesAsync(fullNewPath);
        File.Delete(fullNewPath);

        if (msg.Type == MessageType.Document)
        {
            // Procesar el archivo PDF
            var text = new StringBuilder();
            using (var document = PdfDocument.Open(bytes))
            {
                foreach (var page in document.GetPages())
                {
                    text.Append(page.Text);
                }
            }

            var pdfFinalContent = text.ToString();
            chatHistoryDictionary[msg.Chat.Id].AddUserMessage($"Mensaje del usuario: {msg.Caption} y contenido del fichero es: {pdfFinalContent}");
        }
        else if (msg.Type == MessageType.Photo)
        {
            // Process the image
            var collectionItems = new ChatMessageContentItemCollection
            {
                new TextContent(msg.Caption),
                new ImageContent(new BinaryData(bytes), "image/png")
            };

            chatHistoryDictionary[msg.Chat.Id].AddUserMessage(collectionItems);
        }

        // Send message
        var answer = await chatService.GetChatMessageContentAsync(chatHistoryDictionary[msg.Chat.Id], promptSettings, kernel);
        await bot.SendMessage(msg.Chat, answer.ToString(), ParseMode.Markdown);
    }
}

async Task HandleOptionMessages(TelegramBotClient botClient, Message msg, string command)
{
    switch (command)
    {
        case "/start":
            string welcomeMessage = @"¡Bienvenido al **Asistente Médico Bot**! Somos un servicio diseñado para ayudarte con tus consultas de salud y bienestar. Puedes hacer preguntas relacionadas con medicina, síntomas, tratamientos, y más.";
            await bot.SendMessage(msg.Chat, welcomeMessage, ParseMode.Markdown);
            break;
        case "/help":
            string helpMessage = @"Estamos aquí para ayudarte con tus consultas de salud y bienestar. Puedes hacer preguntas relacionadas con medicina, síntomas, tratamientos, y más. Los comandos disponibles son: /help y /clear";
            await bot.SendMessage(msg.Chat, helpMessage, ParseMode.Markdown);
            break;
        case "/clear":
            // Remove chat history
            chatHistoryDictionary.Remove(msg.Chat.Id);

            // Message to send
            string clearMessage = "Tu historial de chat ha sido limpiado exitosamente.";
            await bot.SendMessage(msg.Chat, clearMessage, ParseMode.Markdown);
            break;
        default:
            string unknownCommand = "Comando desconocido. Usa /help para ver los comandos disponibles.";
            await bot.SendMessage(msg.Chat, unknownCommand, ParseMode.Markdown);
            break;
    }
}

internal class HospitalPlugin
{
    [KernelFunction]
    public List<string> PeopleWhoWorkHere()
    {
        return new List<string> { "Pablo Piovano", "Bruno Capuano", "Emiliano Montesdeoca", "Carla Vanesa" };
    }
}

internal class TelegramPlugin
{
    [KernelFunction]
    [Description("When user needs help and does not what to do")]
    public string HelpNeeded()
    {
        return @"Estamos aquí para ayudarte con tus consultas de salud y bienestar. Puedes hacer preguntas relacionadas con medicina, síntomas, tratamientos, y más. Los comandos disponibles son: /help y /clear";
    }
}
