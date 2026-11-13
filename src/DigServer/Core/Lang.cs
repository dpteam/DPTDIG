using System;
using System.Collections.Generic;

namespace Dig.Core
{
    /// <summary>
    /// Two-language UI table (ru/en). Deliberately tiny: the interface is a search
    /// box and a result list, not a social network. Add a key here and it works on
    /// every page; missing keys fall back to English, then to the key itself.
    /// </summary>
    public sealed class Lang
    {
        private readonly Dictionary<string, string> _map;
        private readonly string _code;

        private Lang(string code, Dictionary<string, string> map) { _code = code; _map = map; }

        public string T(string key)
        {
            string value;
            if (_map.TryGetValue(key, out value)) return value;
            if (Fallback.TryGetValue(key, out value)) return value;
            return key;
        }

        public string Code { get { return _code; } }

        public static Lang For(string code)
        {
            return string.Equals(code, "ru", StringComparison.OrdinalIgnoreCase) ? Ru : En;
        }

        public static readonly Lang En = new Lang("en", BuildEn());
        public static readonly Lang Ru = new Lang("ru", BuildRu());
        private static readonly Dictionary<string, string> Fallback = BuildEn();

        private static Dictionary<string, string> BuildEn()
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.Ordinal);
            d["search"] = "Search";
            d["search_placeholder"] = "ubuntu iso, season pack, album...";
            d["search_hint"] = "Names, file lists and sizes from the public DHT. Nothing is hosted here.";
            d["home"] = "Home";
            d["latest"] = "Latest";
            d["stats"] = "Stats";
            d["about"] = "About";
            d["api"] = "API";
            d["results_for"] = "Results for";
            d["no_results"] = "Nothing found";
            d["no_results_hint"] = "Try fewer words, or check the spelling. The index only knows torrents it has actually seen.";
            d["browse_all"] = "Browse the newest additions";
            d["torrents"] = "torrents";
            d["files"] = "files";
            d["size"] = "Size";
            d["added"] = "Added";
            d["seen"] = "Last seen";
            d["seeders"] = "seeders";
            d["leechers"] = "leechers";
            d["magnet"] = "Magnet link";
            d["copy"] = "Copy";
            d["copied"] = "Copied";
            d["open_magnet"] = "Open in client";
            d["details"] = "Details";
            d["name"] = "Name";
            d["hash"] = "Info hash";
            d["file_list"] = "File list";
            d["path"] = "Path";
            d["page"] = "Page";
            d["prev"] = "Previous";
            d["next"] = "Next";
            d["of"] = "of";
            d["sort"] = "Sort";
            d["sort_relevance"] = "Relevance";
            d["sort_new"] = "Newest";
            d["sort_size"] = "Largest";
            d["sort_seeders"] = "Most seeders";
            d["all"] = "All";
            d["nodes"] = "DHT nodes";
            d["hashes"] = "hashes harvested";
            d["indexed"] = "torrents indexed";
            d["requests"] = "requests served";
            d["uptime"] = "Uptime";
            d["crawler"] = "Crawler";
            d["running"] = "running";
            d["stopped"] = "stopped";
            d["dht_port"] = "UDP port";
            d["queries"] = "queries";
            d["answers"] = "answers";
            d["fetched"] = "metadata fetched";
            d["failed"] = "failed";
            d["queue"] = "queue";
            d["samples"] = "samples";
            d["index_panel"] = "Index";
            d["data_dir"] = "Data dir";
            d["max_torrents"] = "Max torrents";
            d["blocked_hashes"] = "Blocked hashes";
            d["dropped"] = "Dropped";
            d["version"] = "Version";
            d["node_id"] = "Node id";
            d["about_title"] = "What this is";
            d["about_body"] = "This is a search engine over the public BitTorrent DHT. It walks the distributed hash table, harvests info hashes, fetches torrent metadata from peers and indexes names, file lists and sizes. It does not host, store or serve any file, and it cannot remove a torrent from the DHT - it only mirrors what the network already publishes.";
            d["about_how"] = "How it works";
            d["about_how_body"] = "A node joins the mainline DHT over UDP, samples info hashes with BEP 51, harvests announce_peer traffic, then opens a short TCP connection to a peer and asks for the info dictionary (BEP 9). The result is verified against the info hash with SHA-1 before it is indexed.";
            d["abuse_title"] = "Content and abuse";
            d["abuse_body"] = "We do not host content and cannot delete a torrent from the BitTorrent network. If a hash must be hidden from this index, send the exact 40-character info hash and a short explanation to the address below; blocked hashes are removed from search and from the public API.";
            d["no_email"] = "No abuse address configured for this instance.";
            d["status"] = "Status";
            d["source"] = "Source";
            d["language"] = "Language";
            d["theme"] = "Theme";
            d["theme_light"] = "Light";
            d["theme_dark"] = "Dark";
            d["theme_oled"] = "OLED";
            d["search_engine_note"] = "Metadata only, no files";
            d["results_count"] = "results";
            d["in"] = "in";
            d["seconds"] = "s";
            d["never"] = "never";
            d["unknown"] = "unknown";
            d["blocked"] = "This torrent is not available in this index.";
            d["not_found"] = "Not found";
            d["not_found_hint"] = "The hash is not in this index (yet).";
            d["back_home"] = "Back to search";
            d["total_size"] = "Total size";
            d["content"] = "Content";
            d["preview"] = "Preview";
            d["new"] = "new";
            d["ip_intro"] = "Addresses that appeared in public DHT peer lists together with these torrents. This is what the network broadcasts, not a download log.";
            d["ip_torrents"] = "Torrents seen from this address";
            d["ip_empty"] = "No records for this address";
            d["ip_empty_hint"] = "Either it has not been seen yet, or the record aged out of the retention window.";
            d["ip_what"] = "What this means";
            d["ip_what_body"] = "A record only says that this address showed up in a peer list for a torrent at some point. It does not prove a download, and one address can be a whole household, an office, a mobile carrier or a VPN exit shared by thousands of people. We keep the data for a limited time and never link it to an account, because we do not have accounts.";
            d["ip_invalid"] = "Expected an IPv4 address.";
            d["ip_lookup"] = "Address lookup";
            d["ip_seen_from"] = "seen from";
            d["ip_addresses"] = "addresses";
            d["ip_link"] = "Addresses";
            return d;
        }

        private static Dictionary<string, string> BuildRu()
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.Ordinal);
            d["search"] = "Поиск";
            d["search_placeholder"] = "ubuntu iso, сезон сериала, альбом...";
            d["search_hint"] = "Имена, списки файлов и размеры из публичной DHT. Файлы здесь не хранятся.";
            d["home"] = "Главная";
            d["latest"] = "Новое";
            d["stats"] = "Статистика";
            d["about"] = "О проекте";
            d["api"] = "API";
            d["results_for"] = "Результаты по запросу";
            d["no_results"] = "Ничего не найдено";
            d["no_results_hint"] = "Попробуйте меньше слов или проверьте написание. Индекс знает только то, что реально видел в сети.";
            d["browse_all"] = "Смотреть последние добавленные";
            d["torrents"] = "торрентов";
            d["files"] = "файлов";
            d["size"] = "Размер";
            d["added"] = "Добавлен";
            d["seen"] = "Замечен";
            d["seeders"] = "сидов";
            d["leechers"] = "пиров";
            d["magnet"] = "Магнет-ссылка";
            d["copy"] = "Копировать";
            d["copied"] = "Скопировано";
            d["open_magnet"] = "Открыть в клиенте";
            d["details"] = "Подробности";
            d["name"] = "Название";
            d["hash"] = "Info hash";
            d["file_list"] = "Список файлов";
            d["path"] = "Путь";
            d["page"] = "Страница";
            d["prev"] = "Назад";
            d["next"] = "Вперёд";
            d["of"] = "из";
            d["sort"] = "Сортировка";
            d["sort_relevance"] = "По релевантности";
            d["sort_new"] = "Сначала новые";
            d["sort_size"] = "Сначала крупные";
            d["sort_seeders"] = "Больше сидов";
            d["all"] = "Все";
            d["nodes"] = "узлов DHT";
            d["hashes"] = "собрано хешей";
            d["indexed"] = "торрентов в индексе";
            d["requests"] = "запросов обработано";
            d["uptime"] = "Аптайм";
            d["crawler"] = "Краулер";
            d["running"] = "работает";
            d["stopped"] = "остановлен";
            d["dht_port"] = "UDP-порт";
            d["queries"] = "запросов";
            d["answers"] = "ответов";
            d["fetched"] = "метаданных получено";
            d["failed"] = "ошибок";
            d["queue"] = "очередь";
            d["samples"] = "сэмплов";
            d["index_panel"] = "Индекс";
            d["data_dir"] = "Папка данных";
            d["max_torrents"] = "Лимит торрентов";
            d["blocked_hashes"] = "Заблокировано";
            d["dropped"] = "Отброшено";
            d["version"] = "Версия";
            d["node_id"] = "ID узла";
            d["about_title"] = "Что это такое";
            d["about_body"] = "Это поисковик по публичной сети BitTorrent DHT. Он обходит распределённую хеш-таблицу, собирает info hash, забирает метаданные торрента у пиров и индексирует названия, списки файлов и размеры. Он не хранит и не раздаёт файлы и не может удалить торрент из DHT — только отражает то, что сеть уже опубликовала.";
            d["about_how"] = "Как это работает";
            d["about_how_body"] = "Узел подключается к mainline DHT по UDP, сэмплирует info hash через BEP 51, перехватывает announce_peer, затем открывает короткое TCP-соединение к пиру и просит info-словарь (BEP 9). Результат сверяется с info hash по SHA-1 и только потом попадает в индекс.";
            d["abuse_title"] = "Контент и жалобы";
            d["abuse_body"] = "Мы не храним контент и не можем удалить торрент из сети BitTorrent. Если хеш нужно скрыть из этого индекса, пришлите точный 40-символьный info hash и короткое пояснение на адрес ниже; заблокированные хеши исчезают из поиска и публичного API.";
            d["no_email"] = "На этой копии адрес для жалоб не настроен.";
            d["status"] = "Статус";
            d["source"] = "Исходники";
            d["language"] = "Язык";
            d["theme"] = "Тема";
            d["theme_light"] = "Светлая";
            d["theme_dark"] = "Тёмная";
            d["theme_oled"] = "OLED";
            d["search_engine_note"] = "Только метаданные, без файлов";
            d["results_count"] = "результатов";
            d["in"] = "за";
            d["seconds"] = "с";
            d["never"] = "никогда";
            d["unknown"] = "неизвестно";
            d["blocked"] = "Этот торрент недоступен в данном индексе.";
            d["not_found"] = "Не найдено";
            d["not_found_hint"] = "Этого хеша в индексе пока нет.";
            d["back_home"] = "Вернуться к поиску";
            d["total_size"] = "Общий размер";
            d["content"] = "Содержимое";
            d["preview"] = "Просмотр";
            d["new"] = "новое";
            d["ip_intro"] = "Адреса, которые встречались в публичных списках пиров DHT вместе с этими торрентами. Это то, что сеть вещает сама, а не журнал скачиваний.";
            d["ip_torrents"] = "Торренты, замеченные с этого адреса";
            d["ip_empty"] = "Записей по этому адресу нет";
            d["ip_empty_hint"] = "Либо адрес ещё не встречался, либо запись вышла за срок хранения.";
            d["ip_what"] = "Что это значит";
            d["ip_what_body"] = "Запись означает только то, что адрес когда-то появился в списке пиров для торрента. Она не доказывает скачивание, и один адрес может быть целым домом, офисом, мобильным оператором или выходом VPN, общим для тысяч людей. Данные хранятся ограниченное время и никогда не связываются с аккаунтом — аккаунтов у нас нет.";
            d["ip_invalid"] = "Ожидается IPv4-адрес.";
            d["ip_lookup"] = "Поиск по адресу";
            d["ip_seen_from"] = "замечен с";
            d["ip_addresses"] = "адресов";
            d["ip_link"] = "Адреса";
            return d;
        }
    }
}
