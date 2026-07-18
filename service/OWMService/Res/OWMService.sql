CREATE TABLE [dbo].[ows_meteo](
	[WaterStation_id] [uniqueidentifier] NOT NULL,
	[mli] [varchar](64) NULL,
	[country] [char](2) NULL,
	[state] [char](2) NULL,
	[lat] [float] NULL,
	[lon] [float] NULL,
	[ows] [nvarchar](max) NULL,
	[stamp] [datetime2](7) NOT NULL,
PRIMARY KEY CLUSTERED 
(
	[WaterStation_id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY] TEXTIMAGE_ON [PRIMARY]
GO

ALTER TABLE [dbo].[ows_meteo] ADD  DEFAULT (getdate()) FOR [stamp]
GO

ALTER TABLE [dbo].[ows_meteo] CHECK CONSTRAINT [FK_ows_meteo_id]
GO

ALTER TABLE [dbo].[ows_meteo] CHECK CONSTRAINT [FK_ows_meteo_mli]
GO

CREATE TRIGGER [dbo].[TR_ows_meteo] ON [dbo].[ows_meteo] 
FOR UPDATE 
NOT FOR REPLICATION
AS 
SET NOCOUNT ON
BEGIN
	DECLARE  @json nvarchar(max), @mli varchar(64), @WaterStation_id uniqueidentifier
	SELECT TOP 1 @json = ows, @mli = mli, @WaterStation_id = WaterStation_id FROM INSERTED
 	EXEC sp_ows_meteo @json, @mli, @WaterStation_id
END
GO

ALTER TABLE [dbo].[ows_meteo] ENABLE TRIGGER [TR_ows_meteo]
GO

CREATE TABLE [dbo].[weather_Forecast](
	[link] [uniqueidentifier] NOT NULL,
	[tmHigh] [float] NOT NULL,
	[tmLow] [float] NOT NULL,
	[gpfDay] [float] NOT NULL,
	[gpfNight] [float] NOT NULL,
	[humidity] [float] NULL,
	[wind_max_speed] [float] NULL,
	[wind_degree] [float] NULL,
	[wind_direction] [varchar](8) NULL,
	[shortText] [varchar](64) NULL,
	[longText] [varchar](255) NULL,
	[icon] [varchar](255) NULL,
	[pop] [int] NULL,
	[dt] [date] NOT NULL,
	[tm] [time](7) NULL,
	[mli] [varchar](64) NOT NULL,
	[city_id] [int] NULL,
	[pressure] [int] NULL,
	[rain_today] [int] NULL,
	[air_temperature] [int] NULL,
	[tmDay] [float] NULL,
	[weather_code] [int] NULL
) ON [PRIMARY]
GO

/*
	Procedure parse JSON doc and then insert into diffrent tables:
	1. WaterStation - meteo from water station
	2. weather_Forecast

		called from [TR_ows_meteo]
declare @json nvarchar(max) = (select ows from [ows_meteo] where mli = '05MD011')
EXEC sp_ows_meteo @json, '05MD011', '1FB1DA9F-987F-E811-9102-00155D007B11'
*/
CREATE PROCEDURE [dbo].[sp_ows_meteo] @js nvarchar(max), @mli varchar(64), @link uniqueidentifier
AS
SET NOCOUNT ON
BEGIN TRY
	IF @js IS NULL OR @mli IS NULL
	RETURN

declare @moonPhaseCode varchar(max) = (SELECT  REPLACE(REPLACE(JSON_QUERY(@js,'$.moonPhaseCode'), '[',''), ']',''));
declare @moonPhaseDay varchar(max)  = (SELECT  REPLACE(REPLACE(JSON_QUERY(@js,'$.moonPhaseDay'), '[',''), ']',''));
declare @narrative varchar(max)  = (SELECT  REPLACE(REPLACE(JSON_QUERY(@js,'$.narrative'), '[',''), ']',''));
declare @qpf varchar(max)  = (SELECT  REPLACE(REPLACE(JSON_QUERY(@js,'$.qpf'), '[',''), ']',''));
declare @sunriseTimeLocal varchar(max)  = (SELECT  REPLACE(REPLACE(JSON_QUERY(@js,'$.sunriseTimeLocal'), '[',''), ']',''));
declare @sunsetTimeLocal varchar(max)  = (SELECT  REPLACE(REPLACE(JSON_QUERY(@js,'$.sunsetTimeLocal'), '[',''), ']',''));
declare @temperatureMax varchar(max)  = (SELECT  REPLACE(REPLACE(JSON_QUERY(@js,'$.temperatureMax'), '[',''), ']',''));
declare @temperatureMin varchar(max)  = (SELECT  REPLACE(REPLACE(JSON_QUERY(@js,'$.temperatureMin'), '[',''), ']',''));
declare @validTimeLocal varchar(max)  = (SELECT  REPLACE(REPLACE(JSON_QUERY(@js,'$.validTimeLocal'), '[',''), ']',''));
declare @cloudCover varchar(max)  = (SELECT  REPLACE(REPLACE(JSON_QUERY(@js,'$.daypart[0].cloudCover'), '[',''), ']',''));
declare @dayOrNight varchar(max)  = (SELECT  REPLACE(REPLACE(JSON_QUERY(@js,'$.daypart[0].dayOrNight'), '[',''), ']',''));
declare @iconCode varchar(max)  = (SELECT  REPLACE(REPLACE(JSON_QUERY(@js,'$.daypart[0].iconCode'), '[',''), ']',''));
declare @iconCodeExtend varchar(max)  = (SELECT  REPLACE(REPLACE(JSON_QUERY(@js,'$.daypart[0].iconCodeExtend'), '[',''), ']',''));
declare @info varchar(max)  = (SELECT  REPLACE(REPLACE(JSON_QUERY(@js,'$.daypart[0].narrative'), '[',''), ']',''));
declare @precipChance varchar(max)  = (SELECT  REPLACE(REPLACE(JSON_QUERY(@js,'$.daypart[0].precipChance'), '[',''), ']',''));
declare @precipType varchar(max)  = (SELECT  REPLACE(REPLACE(JSON_QUERY(@js,'$.daypart[0].precipType'), '[',''), ']',''));
declare @qpf0 varchar(max)  = (SELECT  REPLACE(REPLACE(JSON_QUERY(@js,'$.daypart[0].qpf'), '[',''), ']',''));
declare @qualifierPhrase varchar(max)  = (SELECT  REPLACE(REPLACE(JSON_QUERY(@js,'$.daypart[0].qualifierPhrase'), '[',''), ']',''));
declare @relativeHumidity varchar(max)  = (SELECT  REPLACE(REPLACE(JSON_QUERY(@js,'$.daypart[0].relativeHumidity'), '[',''), ']',''));
declare @air_temperature varchar(max)  = (SELECT  REPLACE(REPLACE(JSON_QUERY(@js,'$.daypart[0].temperature'), '[',''), ']',''));
declare @wind_degree varchar(max)  = (SELECT  REPLACE(REPLACE(JSON_QUERY(@js,'$.daypart[0].windDirection'), '[',''), ']',''));
declare @win_dir_cardinal varchar(max)  = (SELECT  REPLACE(REPLACE(JSON_QUERY(@js,'$.daypart[0].windDirectionCardinal'), '[',''), ']',''));
declare @wind_max_speed varchar(max)  = (SELECT  REPLACE(REPLACE(JSON_QUERY(@js,'$.daypart[0].windSpeed'), '[',''), ']',''));
	
declare @tbl TABLE (id int not null, narrative varchar(255), qpf float, qpfSnow float
	, sunriseTimeLocal DATETIME2, sunsetTimeLocal DATETIME2, temperatureMax float, temperatureMin float, validTimeLocal DATETIME2
	, cloudCover int, dayOrNight char(1), iconCode int, iconCodeExtend int, info varchar(255), precipChance int, precipType varchar(32)
	, qpf0 float, qualifierPhrase varchar(32), relativeHumidity int, air_temperature int, wind_degree float, wind_max_speed int, win_dir_cardinal varchar(3))

insert into @tbl (id) values (1),(2),(3),(4),(5),(6),(7),(8),(9),(10),(11),(12),(13),(14),(15)

update t set t.win_dir_cardinal = LEFT(COALESCE(x.win_dir_cardinal, ''), 3) FROM @tbl t JOIN 
	(SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) as num, REPLACE(value, '"', '') AS win_dir_cardinal from STRING_SPLIT(@win_dir_cardinal, ','))x ON t.id = x.num

/*
update t set t.moonPhaseDay = x.moonPhaseDay FROM @tbl t JOIN 
	(SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) as num, REPLACE(value, '"', '') AS moonPhaseDay from STRING_SPLIT(@moonPhaseDay, ','))x ON t.id = x.num

update t set t.moonPhaseCode = x.moonPhaseCode FROM @tbl t JOIN 
	(SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) as num, REPLACE(value, '"', '') AS moonPhaseCode from STRING_SPLIT(@moonPhaseCode, ','))x ON t.id = x.num
	*/
update t set t.narrative = x.narrative FROM @tbl t JOIN 
	(SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) as num, REPLACE(value, '"', '') AS narrative from STRING_SPLIT(@narrative, ','))x ON t.id = x.num

update t set t.qpf = (CASE WHEN x.qpf = 'null' THEN 0.0 ELSE CAST(x.qpf AS float) END ) FROM @tbl t JOIN 
	(SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) as num, REPLACE(value, '"', '') AS qpf from STRING_SPLIT(@qpf, ','))x ON t.id = x.num
	
update t set t.sunriseTimeLocal = convert(datetime2, replace(left(x.sunriseTimeLocal, 19), 'T', ' '), 21) FROM @tbl t JOIN 
	(SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) as num, REPLACE(value, '"', '') AS sunriseTimeLocal from STRING_SPLIT(@sunriseTimeLocal, ','))x ON t.id = x.num

update t set t.sunsetTimeLocal = convert(datetime2, replace(left(x.sunsetTimeLocal, 19), 'T', ' '), 21) FROM @tbl t JOIN 
	(SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) as num, REPLACE(value, '"', '') AS sunsetTimeLocal from STRING_SPLIT(@sunsetTimeLocal, ','))x ON t.id = x.num

update t set t.temperatureMax = (CASE WHEN x.temperatureMax = 'null' THEN NULL ELSE x.temperatureMax END) FROM @tbl t JOIN 
	(SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) as num, REPLACE(value, '"', '') AS temperatureMax from STRING_SPLIT(@temperatureMax, ','))x ON t.id = x.num

update t set t.temperatureMin = (CASE WHEN x.temperatureMin = 'null' THEN NULL ELSE x.temperatureMin END) FROM @tbl t JOIN 
	(SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) as num, REPLACE(value, '"', '') AS temperatureMin from STRING_SPLIT(@temperatureMin, ','))x ON t.id = x.num

update t set t.validTimeLocal = convert(datetime2, replace(left(x.validTimeLocal, 19), 'T', ' '), 21) FROM @tbl t JOIN 
	(SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) as num, REPLACE(value, '"', '') AS validTimeLocal from STRING_SPLIT(@validTimeLocal, ','))x ON t.id = x.num

update t set t.cloudCover = (CASE WHEN x.cloudCover = 'null' THEN NULL ELSE x.cloudCover END) FROM @tbl t JOIN 
	(SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) as num, REPLACE(value, '"', '') AS cloudCover from STRING_SPLIT(@cloudCover, ','))x ON t.id = x.num

update t set t.dayOrNight = (CASE WHEN x.dayOrNight = 'null' THEN NULL ELSE x.dayOrNight END) FROM @tbl t JOIN 
	(SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) as num, REPLACE(value, '"', '') AS dayOrNight from STRING_SPLIT(@dayOrNight, ','))x ON t.id = x.num

update t set t.iconCode = (CASE WHEN x.iconCode = 'null' THEN NULL ELSE x.iconCode END) FROM @tbl t JOIN 
	(SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) as num, REPLACE(value, '"', '') AS iconCode from STRING_SPLIT(@iconCode, ','))x ON t.id = x.num

update t set t.iconCodeExtend = (CASE WHEN x.iconCodeExtend = 'null' THEN NULL ELSE x.iconCodeExtend END) FROM @tbl t JOIN 
	(SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) as num, REPLACE(value, '"', '') AS iconCodeExtend from STRING_SPLIT(@iconCodeExtend, ','))x ON t.id = x.num
	
update t set t.info = (CASE WHEN x.info = 'null' THEN NULL ELSE x.info END) FROM @tbl t JOIN 
	(SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) as num, REPLACE(value, '"', '') AS info from STRING_SPLIT(@info, ','))x ON t.id = x.num
	
update t set t.precipChance = (CASE WHEN x.precipChance = 'null' THEN NULL ELSE x.precipChance END) FROM @tbl t JOIN 
	(SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) as num, REPLACE(value, '"', '') AS precipChance from STRING_SPLIT(@precipChance, ','))x ON t.id = x.num
	
update t set t.precipType = (CASE WHEN x.precipType = 'null' THEN NULL ELSE x.precipType END) FROM @tbl t JOIN 
	(SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) as num, REPLACE(value, '"', '') AS precipType from STRING_SPLIT(@precipType, ','))x ON t.id = x.num
	 
update t set t.qpf0 = (CASE WHEN x.qpf0 = 'null' THEN 0.0 ELSE  CAST(x.qpf0 AS float) END)  FROM @tbl t JOIN 
	(SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) as num, REPLACE(value, '"', '') AS qpf0 from STRING_SPLIT(@qpf0, ','))x ON t.id = x.num
		
update t set t.qualifierPhrase = LEFT((CASE WHEN x.qualifierPhrase = 'null' THEN NULL ELSE x.qualifierPhrase END), 24) FROM @tbl t JOIN 
	(SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) as num, REPLACE(value, '"', '') AS qualifierPhrase from STRING_SPLIT(@qualifierPhrase, ','))x ON t.id = x.num
	 
update t set t.relativeHumidity = (CASE WHEN x.relativeHumidity = 'null' THEN NULL ELSE x.relativeHumidity END) FROM @tbl t JOIN 
	(SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) as num, REPLACE(value, '"', '') AS relativeHumidity from STRING_SPLIT(@relativeHumidity, ','))x ON t.id = x.num
 
update t set t.air_temperature = (CASE WHEN x.air_temperature = 'null' THEN NULL ELSE x.air_temperature END) FROM @tbl t JOIN 
	(SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) as num, REPLACE(value, '"', '') AS air_temperature from STRING_SPLIT(@air_temperature, ','))x ON t.id = x.num
--PrintWindDescription()
update t set t.wind_degree = (CASE WHEN x.wind_degree = 'null' THEN NULL ELSE x.wind_degree END) FROM @tbl t JOIN 
	(SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) as num, REPLACE(value, '"', '') AS wind_degree from STRING_SPLIT(@wind_degree, ','))x ON t.id = x.num

update t set t.wind_max_speed = (CASE WHEN x.wind_max_speed = 'null' THEN NULL ELSE x.wind_max_speed END) FROM @tbl t JOIN 
	(SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) as num, REPLACE(value, '"', '') AS wind_max_speed from STRING_SPLIT(@wind_max_speed, ','))x ON t.id = x.num
	 
DELETE FROM @tbl WHERE validTimeLocal IS NULL
UPDATE @tbl SET temperatureMin = COALESCE(temperatureMin,air_temperature,temperatureMax), temperatureMax = COALESCE(temperatureMax,air_temperature,temperatureMin)
  
	MERGE INTO weather_Forecast AS t
        USING @tbl AS source ON t.dt = CAST(source.validTimeLocal AS DATE ) AND t.mli = @mli  
    WHEN MATCHED THEN 
	UPDATE SET t.tmLow = source.temperatureMin, t.tmHigh = source.temperatureMax, t.rain_today = COALESCE(qpf0, qpf)
	 , t.gpfDay = source.qpf, t.gpfNight = source.qpf0, t.air_temperature = source.air_temperature, t.weather_code = source.iconCode
	 , t.wind_degree = source.wind_degree, t.wind_max_speed = source.wind_max_speed, t.wind_direction = source.win_dir_cardinal, t.shortText = LEFT(source.narrative, 64)
    WHEN NOT MATCHED BY TARGET THEN  
        INSERT ( link,  mli,  dt,                           tm,                           tmLow,          tmHigh,         gpfDay, gpfNight,            air_temperature, weather_code, wind_degree, wind_max_speed , wind_direction ) 
		VALUES ( @link, @mli, CAST(validTimeLocal AS DATE), CAST(validTimeLocal AS TIME), temperatureMin, temperatureMax, qpf,   COALESCE(qpf0, qpf), air_temperature, iconCode    , wind_degree, wind_max_speed ,  win_dir_cardinal  );
  
END TRY
BEGIN CATCH
    SELECT ERROR_NUMBER()    AS ErrorNumber,    ERROR_SEVERITY() AS ErrorSeverity, ERROR_STATE()   AS ErrorState
         , ERROR_PROCEDURE() AS ErrorProcedure, ERROR_LINE()     AS ErrorLine,     ERROR_MESSAGE() AS ErrorMessage;
END CATCH;     

GO


