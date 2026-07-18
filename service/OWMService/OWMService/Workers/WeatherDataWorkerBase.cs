namespace OWMService.Workers
{
    using OWMService.Config;
    using OWMService.Logging;
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Data.SqlClient;
    using System.Diagnostics;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Text.RegularExpressions;

    // https://api.weather.gc.ca/
    // https://www.weather.gov/documentation/services-web-api
    // https://developers.google.com/maps/documentation/weather/overview
    // https://www.visualcrossing.com/weather-api/
    // https://weatherstack.com/
    // https://www.meteomatics.com/en/weather-api/

    public abstract class WeatherDataWorkerBase : IWeatherDataWorker
    {
        protected readonly IEventLogger m_logger;
        protected static readonly HttpClient m_httpClient = new HttpClient();
        protected static readonly Regex m_escapeSequenceRegex = new Regex(@"\\""");
        protected const int MinDelayBetweenStationsMs = 2000;

        static WeatherDataWorkerBase()
        {
            // Configure once at static initialization instead of per-request
            ServicePointManager.Expect100Continue = true;
            ServicePointManager.DefaultConnectionLimit = 9999;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        }

        protected WeatherDataWorkerBase(IEventLogger logger)
        {
            m_logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool Process(Settings settings, TimeSpan timeBudget)
        {
            string conStr = settings.GetConnectionString();
            if (string.IsNullOrEmpty(conStr))
            {
                return false;
            }
            try
            {
                m_logger.LogInfo($"Started {GetServiceName()} service.");

                using (SqlConnection cnn = new SqlConnection(conStr))
                {
                    cnn.Open();
                    m_logger.LogInfo("Database connection opened.");

                    string stationQuery = GetStationQuery();
                    List<StationData> stations;
                    try
                    {
                        stations = GetListOwsMeteo(cnn);
                    }
                    catch (Exception ex)
                    {
                        m_logger.LogError($"OWMService Failed to query stations. {ex.Message} Query: {stationQuery}");
                        return false;
                    }
                    m_logger.LogInfo($"Get {stations.Count} OWS stations.");

                    int delayMs = CalculateDelayMs(stations.Count, timeBudget);
                    m_logger.LogInfo($"Time budget: {timeBudget.TotalHours:F1}h, calculated delay per station: {delayMs}ms.");

                    bool apiAuthorized = ProcessEnvData(stations, settings, cnn, delayMs);
                    m_logger.LogInfo($"Read all {stations.Count} OWS stations.");

                    if (!apiAuthorized)
                    {
                        m_logger.LogError("Stopping worker — API key is invalid or expired. Fix the key before restarting.");
                        return false;
                    }

                    ProcessFishState(cnn);
                    m_logger.LogInfo($"Updated all {stations.Count} OWS/Fish related data.");

                    return true;
                }
            }
            catch (Exception ex)
            {
                m_logger.LogError($"OWMService Failed to connect. {ex.Message} at: {conStr}");
                return false;
            }
        }

        /// <summary>
        /// Calculates the delay between station calls so that all stations fit within the time budget.
        /// </summary>
        private int CalculateDelayMs(int stationCount, TimeSpan timeBudget)
        {
            if (stationCount <= 1)
            {
                return MinDelayBetweenStationsMs;
            }

            int totalMs = (int)timeBudget.TotalMilliseconds;
            int delayMs = totalMs / stationCount;

            return Math.Max(delayMs, MinDelayBetweenStationsMs);
        }

        /// <summary>
        /// Processes environment data for all stations.
        /// Returns false if a 401 Unauthorized was encountered (API key invalid).
        /// </summary>
        protected bool ProcessEnvData(List<StationData> stations, Settings settings, SqlConnection cnn, int targetDelayMs)
        {
            bool apiAuthorized = true;

            try
            {
                int i = 0;

                foreach (var item in stations)
                {
                    Stopwatch sw = Stopwatch.StartNew();

                    try
                    {
                        ProcessOWSPoint(item.Mli, item.Latitude, item.Longitude, settings, cnn);
                        i++;
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        m_logger.LogError($"OWMService API returned 401 Unauthorized. Stopping processing. {ex.Message}");
                        apiAuthorized = false;
                        break;
                    }
                    catch (Exception ex)
                    {
                        m_logger.LogError($"OWMService station processing failed. {ex.Message} MLI: {item.Mli} at: {i}");
                    }
                    finally
                    {
                        sw.Stop();
                        int remainingDelay = targetDelayMs - (int)sw.ElapsedMilliseconds;
                        if (remainingDelay > 0)
                        {
                            System.Threading.Thread.Sleep(remainingDelay);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                m_logger.LogError($"OWMService Failed in ProcessEnvData. {ex.Message}");
            }

            return apiAuthorized;
        }

        protected bool ProcessOWSPoint(string mli, float lat, float lon, Settings settings, SqlConnection cnn)
        {
            string url = GetApiUrl(lat, lon, settings);
            string jsonData = ReadJSONOWSData(url);

            if (string.IsNullOrEmpty(jsonData))
            {
                return false;
            }

            return SaveJSONOWSData(jsonData, mli, cnn);
        }

        protected void ProcessFishState(SqlConnection cnn)
        {
            using (SqlCommand cmd = new SqlCommand())
            {
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.Connection = cnn;
                cmd.CommandTimeout = 300; // 5 minutes timeout

                try
                {
                    cmd.CommandText = "spPushSpeciesFromLakeToStation";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = "spTotalUpdateProbability";
                    cmd.ExecuteNonQuery();  // 2 mins
                }
                catch (Exception ex)
                {
                    m_logger.LogError($"ProcessFishState: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Returns the name of the service running the worker.
        /// </summary>
        public abstract string GetServiceName();

        /// <summary>
        /// Returns the SQL query to retrieve weather stations.
        /// </summary>
        public abstract string GetStationQuery();

        protected abstract int GetStationMaxLimitPerDay();

        /// <summary>
        /// Returns the weather source type identifier used in ows_meteo.
        /// </summary>
        protected abstract int GetSourceType();

        /// <summary>
        /// Retrieves weather stations from the database using the query from GetStationQuery().
        /// </summary>
        protected List<StationData> GetListOwsMeteo(SqlConnection cnn)
        {
            var result = new List<StationData>();

            using (SqlCommand cmd = new SqlCommand(GetStationQuery(), cnn))
            using (SqlDataReader dr = cmd.ExecuteReader())
            {
                while (dr.Read())
                {
                    string mli = dr.GetString(0);
                    float lat = (float)dr.GetDouble(1);
                    float lon = (float)dr.GetDouble(2);
                    string state = dr.GetString(3);

                    result.Add(new StationData { Mli = mli, Latitude = lat, Longitude = lon, State = state });
                }
            }

            return result;
        }

        /// <summary>
        /// Builds the weather API URL for the given coordinates.
        /// </summary>
        protected abstract string GetApiUrl(float lat, float lon, Settings settings);

        /// <summary>
        /// Fetches weather JSON data from the given URL.
        /// Throws UnauthorizedAccessException on HTTP 401 to stop processing.
        /// </summary>
        protected string ReadJSONOWSData(string url)
        {
            System.Threading.Thread.Sleep(1000);
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = 30000;
                request.ReadWriteTimeout = 30000;

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        m_logger.LogError($"ReadJSONOWSData: HTTP {response.StatusCode}");
                        return "";
                    }

                    using (Stream responseStream = response.GetResponseStream())
                    using (StreamReader reader = new StreamReader(responseStream))
                    {
                        string result = reader.ReadToEnd();
                        result = m_escapeSequenceRegex.Replace(result, "\"");
                        return result;
                    }
                }
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse resp
                && resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                m_logger.LogError($"ReadJSONOWSData: 401 Unauthorized - API key is invalid or expired.");
                throw new UnauthorizedAccessException("API returned 401 Unauthorized.", ex);
            }
            catch (Exception ex)
            {
                m_logger.LogError($"ReadJSONOWSData: {ex.Message}");
            }

            return "";
        }

        /// <summary>
        /// Saves fetched weather JSON data to ows_meteo using the type from GetSourceType().
        /// </summary>
        protected bool SaveJSONOWSData(string jsonData, string mli, SqlConnection cnn)
        {
            if (string.IsNullOrEmpty(jsonData) || string.IsNullOrEmpty(mli) || cnn == null)
            {
                return false;
            }

            using (SqlCommand cmd = new SqlCommand())
            {
                cmd.CommandType = CommandType.Text;
                cmd.Connection = cnn;
                cmd.CommandText = "UPDATE ows_meteo SET type = @type, ows = @js, stamp=GETDATE() WHERE mli = @mli";
                cmd.Parameters.Add("@type", SqlDbType.Int).Value = GetSourceType();
                cmd.Parameters.Add("@js", SqlDbType.NVarChar).Value = jsonData;
                cmd.Parameters.Add("@mli", SqlDbType.VarChar).Value = mli;

                try
                {
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    m_logger.LogError($"SaveJSONOWSData: {ex.Message}");
                    DumpFailedJson(mli, jsonData);
                    return false;
                }
            }

            m_logger.LogInfo($"Processed {mli} station.");
            return true;
        }

        /// <summary>
        /// Writes the JSON payload to a file in the log folder when SaveJSONOWSData fails.
        /// File name: failed_{mli}_{timestamp}.json
        /// </summary>
        private void DumpFailedJson(string mli, string jsonData)
        {
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "OWMService", "Logs");

                if (!Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                string fileName = $"failed_{mli}_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                string filePath = Path.Combine(logDir, fileName);

                File.WriteAllText(filePath, jsonData);
                m_logger.LogInfo($"Saved failed JSON for station {mli} to {filePath}");
            }
            catch (Exception ex)
            {
                m_logger.LogError($"DumpFailedJson: Could not save JSON for {mli}. {ex.Message}");
            }
        }
    }
}