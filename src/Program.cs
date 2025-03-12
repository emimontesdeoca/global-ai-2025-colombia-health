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

// Add services to the container.
var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseHttpsRedirection();

// Variables
var endpoint = Environment.GetEnvironmentVariable("OPENAI_ENDPOINT", EnvironmentVariableTarget.User);
var apikey = Environment.GetEnvironmentVariable("OPENAI_APIKEY", EnvironmentVariableTarget.User);
var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT", EnvironmentVariableTarget.User);
var deploymentName = "gpt-4o-mini";

// Bot settings
var bot = new TelegramBotClient(token!, cancellationToken: new CancellationTokenSource().Token);
var me = await bot.GetMe();

var kernelBuilder = Kernel.CreateBuilder();
var chatService = new AzureOpenAIChatCompletionService(deploymentName, endpoint!, apikey!);

kernelBuilder.Plugins.AddFromType<TelegramPlugin>();
kernelBuilder.Plugins.AddFromType<MedicalAppointmentsPlugin>();
kernelBuilder.Plugins.AddFromType<MedicamentosPlugin>();

var promptSettings = new AzureOpenAIPromptExecutionSettings()
{
    ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions
};

var kernel = kernelBuilder.Build();

// System prompt
var systemPrompt = @"Eres un asistente médico experto. Solo debes responder a preguntas relacionadas con la salud, medicina y bienestar. Tienes la capacidad de analizar imágenes y archivos que se te envíen, proporcionando una descripción y posibles interpretaciones sobre su contenido médico. Puedes ofrecer recomendaciones generales sobre el manejo de síntomas comunes, como la fiebre, pero siempre enfatizando que es importante consultar a un profesional médico para una evaluación específica. 

Además, puedes gestionar citas médicas. Tienes la capacidad de reservar, cancelar y listar citas médicas según lo solicitado por el paciente. 

Si una pregunta no está relacionada con estos temas, responde educadamente indicándolo. Sin embargo, si la pregunta está relacionada con algo que ya te ha contado el paciente o está en el historial de conversación, puedes dar una respuesta adecuada. Al analizar imágenes como radiografías o tomografías, aclara qué podrías inferir sobre la condición del paciente basándote en esa imagen.";

// Chat history
var history = new ChatHistory();
history.AddSystemMessage(systemPrompt);

// Link events
bot.OnMessage += OnMessage;

async Task OnMessage(Message msg, UpdateType type)
{
    var collectionItems = new ChatMessageContentItemCollection();

    if (msg.Type == MessageType.Text)
    {
        collectionItems.Add(new TextContent(msg.Text));
    }

    if (msg.Type == MessageType.Document || msg.Type == MessageType.Photo)
    {
        var bytes = await GetFile(msg);

        collectionItems.Add(new TextContent(msg.Caption ?? ""));

        if (msg.Type == MessageType.Document)
        {
            var contentsFromPdf = ReadPdf(bytes);
            collectionItems.Add(new TextContent(contentsFromPdf));
        }

        if (msg.Type == MessageType.Photo)
        {
            collectionItems.Add(new ImageContent(new BinaryData(bytes), "image/png"));
        }
    }

    history.AddUserMessage(collectionItems);

    var answer = await chatService.GetChatMessageContentAsync(history, promptSettings, kernel);
    await bot.SendMessage(msg.Chat, answer.ToString());
}

app.Run();

string ReadPdf(byte[] bytes)
{
    var text = new StringBuilder();
    using (var document = PdfDocument.Open(bytes))
    {
        foreach (var page in document.GetPages())
        {
            text.Append(page.Text);
        }
    }

    return text.ToString();
}

async Task<byte[]> GetFile(Message msg)
{
    // Get file stuff
    var fileId = msg.Type == MessageType.Document ? msg.Document.FileId : msg.Photo.Last().FileId;
    var fileInfo = await bot.GetFile(fileId);
    var filePath = fileInfo.FilePath!;

    // Random path
    var fullNewPath = Path.Combine(Directory.GetCurrentDirectory(), Guid.NewGuid().ToString());

    // Download file
    await using (var fileStream = File.Create(fullNewPath))
    {
        await bot.DownloadFile(filePath, fileStream);
    }

    var bytes = await File.ReadAllBytesAsync(fullNewPath);
    File.Delete(fullNewPath);

    return bytes;
}

public class TelegramPlugin()
{
    [KernelFunction("startup")]
    [Description("When user comes to the system or the message is /start")]
    public string Startup()
    {
        return @"Estamos aquí para ayudarte con tus consultas de salud y bienestar. Puedes hacer preguntas relacionadas con medicina, síntomas, tratamientos, y más. Los comandos disponibles son: /help y /clear";
    }
}
public class MedicalAppointmentsPlugin
{
    [KernelFunction("book_appointment")]
    [Description("Books a new medical appointment for the user.")]
    public async Task<string> BookAppointmentAsync(
        [Description("Appointment details (e.g., doctor name, date, time)")] string appointmentDetails)
    {
        if (string.IsNullOrWhiteSpace(appointmentDetails))
            return "Error: Appointment details cannot be empty.";

        AppointmentService.AddAppointment(appointmentDetails);
        return $"Appointment booked successfully: {appointmentDetails}";
    }

    [KernelFunction("cancel_appointment")]
    [Description("Cancels an existing medical appointment.")]
    public async Task<string> CancelAppointmentAsync(
        [Description("Appointment details to cancel (e.g., doctor name, date, time)")] string appointmentDetails)
    {
        if (AppointmentService.AppointmentItems.Remove(appointmentDetails))
            return $"Appointment canceled successfully: {appointmentDetails}";

        return $"Error: No appointment found for details: {appointmentDetails}";
    }

    [KernelFunction("list_appointments")]
    [Description("Lists all medical appointments.")]
    public async Task<string> ListAppointmentsAsync()
    {
        if (AppointmentService.AppointmentItems.Count == 0)
            return "No appointments found.";

        return "Appointments:\n" + string.Join("\n", AppointmentService.AppointmentItems);
    }
}
public class AppointmentService
{
    public static List<string> AppointmentItems { get; set; } = new List<string>();

    public static void AddAppointment(string appointment) => AppointmentItems.Add(appointment);
}
public class MedicamentosPlugin
{
    [KernelFunction("medicamentos_get")]
    [Description("Medicine the user can request")]
    public string GetMedicamentos()
    {
        return "Ibuprofeno, parecetamol, amoxicilina";
    }
}
