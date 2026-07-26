using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;

namespace HttpSpy.Core.Localization;

/// <summary>The languages the interface ships in.</summary>
public enum AppLanguage
{
    /// <summary>Follow the operating system's UI language.</summary>
    System,
    English,
    Russian,
}

/// <summary>
/// The interface string catalogue.
///
/// Strings are held here rather than inline in the views so the language can be
/// switched at runtime: the catalogue is an indexable, observable object, and
/// a XAML markup extension binds to it, so flipping the
/// language re-evaluates every bound string without restarting.
///
/// A key with no translation falls back to English, and an unknown key renders
/// as the key itself — a missing string should look obviously wrong in the
/// interface rather than silently rendering as blank.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Current { get; } = new();

    private AppLanguage _language = AppLanguage.System;

    private Loc() { }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the language changes, for code that caches strings.</summary>
    public event Action? LanguageChanged;

    public AppLanguage Language
    {
        get => _language;
        set
        {
            if (_language == value) return;
            _language = value;

            // Re-evaluate every localized binding. "Item[]" is the conventional
            // "all indexer results changed" signal, but the binding engine only
            // reliably matches the exact indexer name, so each key is announced
            // individually as well — a few hundred events on a deliberate
            // language switch is nothing, and a half-translated window is the
            // failure this exists to prevent.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            foreach (var key in English.Keys)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs($"Item[{key}]"));

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            LanguageChanged?.Invoke();
        }
    }

    /// <summary>The language actually in effect once <see cref="AppLanguage.System"/> is resolved.</summary>
    public AppLanguage Effective => _language == AppLanguage.System ? DetectSystemLanguage() : _language;

    private static AppLanguage DetectSystemLanguage() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.Russian
            : AppLanguage.English;

    /// <summary>Looks up a key in the current language.</summary>
    public string this[string key] => Get(key);

    /// <summary>Looks up a key in the current language, falling back to English then to the key.</summary>
    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;

        if (Effective == AppLanguage.Russian && Russian.TryGetValue(key, out var ru)) return ru;
        return English.TryGetValue(key, out var en) ? en : key;
    }

    /// <summary>Parses a persisted language name; unknown values fall back to following the system.</summary>
    public static AppLanguage Parse(string? name) => name?.ToLowerInvariant() switch
    {
        "en" or "english" => AppLanguage.English,
        "ru" or "russian" or "русский" => AppLanguage.Russian,
        _ => AppLanguage.System,
    };

    /// <summary>The short form persisted in settings.</summary>
    public static string ToCode(AppLanguage language) => language switch
    {
        AppLanguage.English => "en",
        AppLanguage.Russian => "ru",
        _ => "system",
    };

    /// <summary>Display names for the language picker, in catalogue order.</summary>
    public static IReadOnlyList<string> LanguageNames { get; } =
        new[] { "System default", "English", "Русский" };

    /// <summary>True when a key has a Russian translation — used by the coverage test.</summary>
    internal static bool HasRussian(string key) => Russian.ContainsKey(key);

    internal static IReadOnlyDictionary<string, string> EnglishCatalogue => English;
    internal static IReadOnlyDictionary<string, string> RussianCatalogue => Russian;

    // ---- Catalogue -----------------------------------------------------------
    // Keys are grouped by where they appear. Keep the two dictionaries in the
    // same order; the coverage test fails if a key is added to one and not the
    // other, which is what stops the Russian build drifting into half-English.

    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        // Toolbar
        ["Toolbar.Start"] = "Start capture",
        ["Toolbar.Stop"] = "Stop capture",
        ["Toolbar.Clear"] = "Clear",
        ["Toolbar.Session"] = "Session ▾",
        ["Toolbar.Capture"] = "Capture ▾",
        ["Toolbar.Certificate"] = "Certificate ▾",
        ["Toolbar.More"] = "⋯",
        ["Toolbar.Theme"] = "Toggle dark / light theme",

        // Tabs
        ["Tab.Capture"] = "Capture",
        ["Tab.Structure"] = "Structure",
        ["Tab.Dashboard"] = "Dashboard",
        ["Tab.Analysis"] = "Analysis",
        ["Tab.Submitter"] = "Submitter",
        ["Tab.Rules"] = "Rules",
        ["Tab.Log"] = "Log",

        // Filter bar
        ["Filter.Placeholder"] = "Filter by URL, host or process…",
        ["Filter.Search"] = "Search headers and bodies…",
        ["Filter.All"] = "All",
        ["Filter.AllTypes"] = "All types",
        ["Filter.AllHosts"] = "All hosts",
        ["Filter.AllProcesses"] = "All processes",
        ["Filter.ErrorsOnly"] = "Errors only",
        ["Filter.Type"] = "Type",
        ["Filter.Host"] = "Host",
        ["Filter.Process"] = "Process",
        ["Filter.Method"] = "Method",
        ["Filter.Clear"] = "Clear filters",

        // Grid columns
        ["Column.Index"] = "#",
        ["Column.Result"] = "Result",
        ["Column.Protocol"] = "Proto",
        ["Column.Method"] = "Method",
        ["Column.Host"] = "Host",
        ["Column.Url"] = "URL",
        ["Column.Type"] = "Type",
        ["Column.Size"] = "Size",
        ["Column.Time"] = "Time",
        ["Column.Speed"] = "Speed",
        ["Column.Server"] = "Server",
        ["Column.Connection"] = "Conn",
        ["Column.Process"] = "Process",

        // Status bar
        ["Status.Port"] = "Port",
        ["Status.PortTip"] = "TCP port the proxy listens on. Takes effect the next time you start the capture.",
        ["Status.CertTip"] = "HTTPS decryption only works once the HttpSpy root CA is trusted by the client",
        ["Status.AutosaveTip"] = "The capture is snapshotted periodically and offered back after an unclean exit",
        ["Status.Ready"] = "Ready",
        ["Stats.Recording"] = "REC",
        ["Stats.Stopped"] = "Stopped",
        ["Stats.Sessions"] = "Sessions",
        ["Stats.Shown"] = "shown",
        ["Stats.Connections"] = "Active conns",
        ["Stats.In"] = "In",
        ["Stats.Out"] = "Out",
        ["Stats.Errors"] = "Errors",
        ["Status.Capturing"] = "Capturing on",
        ["Cert.Trusted"] = "Root CA trusted",
        ["Cert.NotTrusted"] = "Root CA not trusted",
        ["Cert.Unknown"] = "Root CA — trust unverified",

        // Menus
        ["Toolbar.Analyse"] = "Analyse",
        ["Filter.FindNext"] = "Find next",
        ["Empty.NoSelection"] = "Select a session to inspect it",
        ["Empty.NoSelectionHint"] =
            "Headers, cookies, query and form fields, the decoded body, a JSON tree, the timing waterfall " +
            "and generated client code all appear here.",
        ["Menu.File"] = "_File",
        ["Menu.CaptureMenu"] = "_Capture",
        ["Menu.SessionMenu"] = "_Session",
        ["Menu.View"] = "_View",
        ["Menu.Https"] = "_HTTPS",
        ["Menu.Tools"] = "_Tools",
        ["Menu.Help"] = "_Help",
        ["Menu.OpenSession"] = "Open session…",
        ["Menu.SaveSession"] = "Save session…",
        ["Menu.Autosave"] = "Autosave capture",
        ["Menu.ImportTraffic"] = "Import HAR / .http / capture…",
        ["Menu.ImportCurl"] = "Import cURL from clipboard",
        ["Menu.ExportHar"] = "Export HAR 1.2…",
        ["Menu.ExportCsv"] = "Export CSV…",
        ["Menu.ExportJson"] = "Export JSON…",
        ["Menu.ExportXml"] = "Export XML…",
        ["Menu.ExportTxt"] = "Export plain text…",
        ["Menu.ExportSeparate"] = "One raw file per session…",
        ["Menu.TrustCert"] = "Trust root certificate",
        ["Menu.ExportCert"] = "Export root certificate (.pem / .cer)…",
        ["Menu.CertInfo"] = "Certificate info / copy thumbprint",
        ["Menu.SetupGuide"] = "Setup guide for this platform…",
        ["Menu.RemoveTrust"] = "Remove trust",
        ["Menu.SearchAll"] = "Search all transactions…",
        ["Menu.Options"] = "Options / Network simulation…",
        ["Menu.DisplayFilters"] = "Display filters…",
        ["Menu.Converter"] = "Converter (URL / Base64 / Hex / JSON)…",
        ["Menu.RegexTester"] = "Regular expression tester…",
        ["Menu.CaptureFilters"] = "Capture filters…",
        ["Menu.ExportSettings"] = "Export settings and rules…",
        ["Menu.ImportSettings"] = "Import settings and rules…",
        ["Menu.Shortcuts"] = "Keyboard shortcuts",
        ["Menu.GettingStarted"] = "Getting started",

        // Empty states
        ["Empty.NoTraffic"] = "No traffic captured yet",
        ["Empty.NoTrafficHint"] =
            "Press F5 (or \u201cStart capture\u201d), then send traffic through 127.0.0.1 on the port shown in the toolbar. " +
            "To decrypt HTTPS, first use Certificate \u25b8 Trust root certificate.",
        ["Empty.NoChart"] = "Nothing to chart yet",
        ["Empty.NoAnalysis"] = "Nothing analysed yet",

        // Common buttons
        ["Search.Title"] = "Search all transactions",
        ["Search.Placeholder"] = "text to find in URLs, headers, bodies and messages",
        ["Search.Go"] = "Search",
        ["Search.Regex"] = "Regex",
        ["Search.MatchCase"] = "Match case",
        ["Search.WholeWord"] = "Whole word",
        ["Search.LookIn"] = "Look in:",
        ["Search.Urls"] = "URLs",
        ["Search.Headers"] = "Headers",
        ["Search.Bodies"] = "Bodies",
        ["Search.Messages"] = "WS / SSE",
        ["Search.GoToTransaction"] = "Go to transaction",
        ["Search.TypeSomething"] = "Type something to search for.",
        ["Search.NoScope"] = "Nothing selected to look in.",
        ["Search.Searching"] = "Searching…",
        ["Common.Apply"] = "Apply",
        ["Common.Cancel"] = "Cancel",
        ["Common.Close"] = "Close",
        ["Common.Save"] = "Save",
        ["Common.Delete"] = "Delete",
        ["Common.Add"] = "Add",
        ["Common.Language"] = "Language",
        ["Common.RestartHint"] = "Applies immediately.",
    };

    private static readonly Dictionary<string, string> Russian = new(StringComparer.Ordinal)
    {
        // Toolbar
        ["Toolbar.Start"] = "Начать захват",
        ["Toolbar.Stop"] = "Остановить захват",
        ["Toolbar.Clear"] = "Очистить",
        ["Toolbar.Session"] = "Сессия ▾",
        ["Toolbar.Capture"] = "Захват ▾",
        ["Toolbar.Certificate"] = "Сертификат ▾",
        ["Toolbar.More"] = "⋯",
        ["Toolbar.Theme"] = "Переключить тёмную / светлую тему",

        // Tabs
        ["Tab.Capture"] = "Захват",
        ["Tab.Structure"] = "Структура",
        ["Tab.Dashboard"] = "Сводка",
        ["Tab.Analysis"] = "Анализ",
        ["Tab.Submitter"] = "Запросы",
        ["Tab.Rules"] = "Правила",
        ["Tab.Log"] = "Журнал",

        // Filter bar
        ["Filter.Placeholder"] = "Фильтр по URL, хосту или процессу…",
        ["Filter.Search"] = "Поиск по заголовкам и телам…",
        ["Filter.All"] = "Все",
        ["Filter.AllTypes"] = "Все типы",
        ["Filter.AllHosts"] = "Все хосты",
        ["Filter.AllProcesses"] = "Все процессы",
        ["Filter.ErrorsOnly"] = "Только ошибки",
        ["Filter.Type"] = "Тип",
        ["Filter.Host"] = "Хост",
        ["Filter.Process"] = "Процесс",
        ["Filter.Method"] = "Метод",
        ["Filter.Clear"] = "Сбросить фильтры",

        // Grid columns
        ["Column.Index"] = "#",
        ["Column.Result"] = "Код",
        ["Column.Protocol"] = "Прот.",
        ["Column.Method"] = "Метод",
        ["Column.Host"] = "Хост",
        ["Column.Url"] = "URL",
        ["Column.Type"] = "Тип",
        ["Column.Size"] = "Размер",
        ["Column.Time"] = "Время",
        ["Column.Speed"] = "Скорость",
        ["Column.Server"] = "Сервер",
        ["Column.Connection"] = "Соед.",
        ["Column.Process"] = "Процесс",

        // Status bar
        ["Status.Port"] = "Порт",
        ["Status.PortTip"] = "TCP-порт, который слушает прокси. Применится при следующем запуске захвата.",
        ["Status.CertTip"] = "Расшифровка HTTPS работает только после того, как клиент начнёт доверять корневому сертификату HttpSpy",
        ["Status.AutosaveTip"] = "Захват периодически сохраняется и будет предложен обратно после аварийного завершения",
        ["Status.Ready"] = "Готово",
        ["Stats.Recording"] = "ЗАПИСЬ",
        ["Stats.Stopped"] = "Остановлен",
        ["Stats.Sessions"] = "Транзакций",
        ["Stats.Shown"] = "показано",
        ["Stats.Connections"] = "Соединений",
        ["Stats.In"] = "Вх",
        ["Stats.Out"] = "Исх",
        ["Stats.Errors"] = "Ошибок",
        ["Status.Capturing"] = "Захват на",
        ["Cert.Trusted"] = "Корневой сертификат доверенный",
        ["Cert.NotTrusted"] = "Корневой сертификат не доверенный",
        ["Cert.Unknown"] = "Доверие к корневому сертификату не проверено",

        // Menus
        ["Toolbar.Analyse"] = "Анализ",
        ["Filter.FindNext"] = "Далее",
        ["Empty.NoSelection"] = "Выберите транзакцию для просмотра",
        ["Empty.NoSelectionHint"] =
            "Заголовки, куки, параметры запроса и формы, декодированное тело, дерево JSON, диаграмма таймингов " +
            "и сгенерированный клиентский код появятся здесь.",
        ["Menu.File"] = "_Файл",
        ["Menu.CaptureMenu"] = "_Захват",
        ["Menu.SessionMenu"] = "_Транзакция",
        ["Menu.View"] = "_Вид",
        ["Menu.Https"] = "_HTTPS",
        ["Menu.Tools"] = "_Инструменты",
        ["Menu.Help"] = "_Справка",
        ["Menu.OpenSession"] = "Открыть сессию…",
        ["Menu.SaveSession"] = "Сохранить сессию…",
        ["Menu.Autosave"] = "Автосохранение захвата",
        ["Menu.ImportTraffic"] = "Импорт HAR / .http / захвата…",
        ["Menu.ImportCurl"] = "Импорт cURL из буфера обмена",
        ["Menu.ExportHar"] = "Экспорт HAR 1.2…",
        ["Menu.ExportCsv"] = "Экспорт CSV…",
        ["Menu.ExportJson"] = "Экспорт JSON…",
        ["Menu.ExportXml"] = "Экспорт XML…",
        ["Menu.ExportTxt"] = "Экспорт в текст…",
        ["Menu.ExportSeparate"] = "По одному файлу на сессию…",
        ["Menu.TrustCert"] = "Доверять корневому сертификату",
        ["Menu.ExportCert"] = "Экспорт корневого сертификата (.pem / .cer)…",
        ["Menu.CertInfo"] = "Сведения о сертификате / копировать отпечаток",
        ["Menu.SetupGuide"] = "Инструкция по настройке для этой системы…",
        ["Menu.RemoveTrust"] = "Отозвать доверие",
        ["Menu.SearchAll"] = "Искать во всех транзакциях…",
        ["Menu.Options"] = "Параметры / эмуляция сети…",
        ["Menu.DisplayFilters"] = "Фильтры отображения…",
        ["Menu.Converter"] = "Конвертер (URL / Base64 / Hex / JSON)…",
        ["Menu.RegexTester"] = "Тестер регулярных выражений…",
        ["Menu.CaptureFilters"] = "Фильтры захвата…",
        ["Menu.ExportSettings"] = "Экспорт настроек и правил…",
        ["Menu.ImportSettings"] = "Импорт настроек и правил…",
        ["Menu.Shortcuts"] = "Горячие клавиши",
        ["Menu.GettingStarted"] = "С чего начать",

        // Empty states
        ["Empty.NoTraffic"] = "Трафик ещё не захвачен",
        ["Empty.NoTrafficHint"] =
            "Нажмите F5 (или \u00abНачать захват\u00bb) и направьте трафик на 127.0.0.1, порт указан в строке состояния. " +
            "Чтобы расшифровывать HTTPS, сначала выполните Сертификат \u25b8 Доверять корневому сертификату.",
        ["Empty.NoChart"] = "Пока нечего показывать",
        ["Empty.NoAnalysis"] = "Анализ ещё не выполнялся",

        // Common buttons
        ["Search.Title"] = "Поиск по всем транзакциям",
        ["Search.Placeholder"] = "что искать в URL, заголовках, телах и сообщениях",
        ["Search.Go"] = "Искать",
        ["Search.Regex"] = "Регулярное выражение",
        ["Search.MatchCase"] = "Учитывать регистр",
        ["Search.WholeWord"] = "Слово целиком",
        ["Search.LookIn"] = "Искать в:",
        ["Search.Urls"] = "URL",
        ["Search.Headers"] = "заголовках",
        ["Search.Bodies"] = "телах",
        ["Search.Messages"] = "WS / SSE-сообщениях",
        ["Search.GoToTransaction"] = "Перейти к транзакции",
        ["Search.TypeSomething"] = "Введите, что нужно найти.",
        ["Search.NoScope"] = "Не выбрано, где искать.",
        ["Search.Searching"] = "Идёт поиск…",
        ["Common.Apply"] = "Применить",
        ["Common.Cancel"] = "Отмена",
        ["Common.Close"] = "Закрыть",
        ["Common.Save"] = "Сохранить",
        ["Common.Delete"] = "Удалить",
        ["Common.Add"] = "Добавить",
        ["Common.Language"] = "Язык",
        ["Common.RestartHint"] = "Применяется сразу.",
    };
}
