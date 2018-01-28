﻿using Belgrade.SqlClient;
using Belgrade.SqlClient.SqlDb.Rls;
using BSCT;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Xunit;

namespace Basic
{
    public class Query
    {
        IQueryPipe pipe;
        IQueryMapper mapper;
        ICommand command;
        public Query()
        {
            pipe = new Belgrade.SqlClient.SqlDb.QueryPipe(Util.Settings.ConnectionString);
            mapper = new Belgrade.SqlClient.SqlDb.QueryMapper(Util.Settings.ConnectionString);
            command = new Belgrade.SqlClient.SqlDb.Command(Util.Settings.ConnectionString);
        }

        [Theory, PairwiseData]
        public void ReturnsJsonParallel([CombinatorialValues("stream", "writer", "mapper", "command")] string client,
                                       [CombinatorialValues("auto", "path")] string mode1,
                                       [CombinatorialValues(",include_null_values", ",root('test')", ",root")] string mode2,
                                       [CombinatorialValues("1", null)] string defaultValue,
                                       [CombinatorialValues("[/]", "{\"a\":/}", "/")] string wrapper,
                                       [CombinatorialValues("TEST", "2")] string sessionContext1,
                                       [CombinatorialValues("TEST", "1")] string sessionContext2)
        {
            List<Task> tasks = new List<Task>();            
            for (int i = 0; i <= 100; i++)
                tasks.Add(Task.Run(() => ReturnsJson(client, (i%2==0)?"1":"1000", mode1, mode2, i%3==0, i%3>0, defaultValue, wrapper, sessionContext1, sessionContext2)));

            Task.WaitAll(tasks.ToArray());

            foreach (var t in tasks)
            {
                Assert.False(t.IsCanceled);
                Assert.True(t.IsCompleted);
                Assert.False(t.IsFaulted);
            }
        }

        //[Theory, CombinatorialData]
        [Theory, PairwiseData]
        public async Task ReturnsJson( [CombinatorialValues("stream", "writer", "mapper", "command")] string client,
                                       [CombinatorialValues(1, 50, 10000)] string top, 
                                       [CombinatorialValues("auto","path")] string mode1,
                                       [CombinatorialValues(",include_null_values", ",root('test')", ",root")] string mode2,
                                       [CombinatorialValues(true, false)] bool useCommand,
                                       [CombinatorialValues(true, false)] bool useAsync,
                                       [CombinatorialValues("1", null)] string defaultValue,
                                       [CombinatorialValues("[/]", "{\"a\":/}", "/")] string wrapper,
                                       [CombinatorialValues("", "test")] string sessionContext1,
                                       [CombinatorialValues("TEST", null)] string sessionContext2
                                )
        {
            // Arrange
            var pipe = new Belgrade.SqlClient.SqlDb.QueryPipe(Util.Settings.ConnectionString)
                .AddContextVariable("v1", () => sessionContext1)
                .AddContextVariable("v2", () => sessionContext2)
                .OnError(ex=> { throw ex; });
            var mapper = new Belgrade.SqlClient.SqlDb.QueryMapper(Util.Settings.ConnectionString)
                .AddContextVariable("v1", () => sessionContext1)
                .AddContextVariable("v2", () => sessionContext2)
                .OnError(ex => { throw ex; });
            var command = new Belgrade.SqlClient.SqlDb.Command(Util.Settings.ConnectionString)
                .AddContextVariable("v1", () => sessionContext1)
                .AddContextVariable("v2", () => sessionContext2)
                .OnError(ex => { throw ex; });

            var sql = @"select top " + top + @" v1 = cast(SESSION_CONTEXT(N'v1') as varchar(500)), 
                                                v2 = cast(SESSION_CONTEXT(N'v2') as varchar(500)),
                            o.object_id tid, o.name t_name, o.schema_id, o.type t, o.type_desc, o.create_date cd, o.modify_date md,
                            c.*
                        from sys.objects o, sys.columns c for json " + mode1 + mode2;
            string json = "INVALID JSON";
            var pair = wrapper.Split('/');
            string prefix = pair[0];
            string suffix = pair[1];
            Task t = null;

            // Action
            if (client == "stream")
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    if (useCommand)
                        t = pipe.Stream(new SqlCommand(sql), ms);
                    else
                        t = pipe.Stream(sql, ms);

                    if (useAsync)
                        await t;
                    else
                        t.Wait();

                    ms.Position = 0;
                    json = new StreamReader(ms).ReadToEnd();
                }
            } else if (client == "writer")
            {
                using (var sw = new StringWriter())
                {
                    if (useCommand)
                        t = pipe.Stream(new SqlCommand(sql), sw, new Options() { Prefix = prefix, DefaultOutput = defaultValue, Suffix = suffix });
                    else
                        t = pipe.Stream(sql, sw, new Options() { Prefix = prefix, DefaultOutput = defaultValue, Suffix = suffix });

                    if (useAsync)
                        await t;
                    else
                        t.Wait();

                    json = sw.ToString();
                }
            } else if (client == "command")
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    if (useCommand)
                        t = command.Stream(new SqlCommand(sql), ms, "");
                    else
                        t = command.Stream(sql, ms, "");

                    if (useAsync)
                        await t;
                    else
                        t.Wait();

                    ms.Position = 0;
                    json = new StreamReader(ms).ReadToEnd();
                }
            } else
            {
                if (!useAsync)
                    return;
                if(useCommand)
                    json = await mapper.GetStringAsync(new SqlCommand(sql));
                else
                    json = await mapper.GetStringAsync(sql);
            }

            // Assert
            AssertEx.IsValidJson(json);
            if (json.StartsWith("["))
            {
                string v1 = null;
                var b = JArray.Parse(json).SelectTokens("..v1");
                var f = b.First();
                v1 = (string)f;
                /*
                if (f is JValue)
                    v1 = (string)f;
                else
                    v1 = f.Value<string>();
                    */
                Assert.Equal(sessionContext1, v1);
            } else
            {
                string v1 = null;
                var a = JObject.Parse(json);
                var b = a.SelectTokens("..v1");
                var f = b.First();
                v1 = (string)f;
                /*
                if (f is JValue)
                    v1 = (string)f;
                else
                    v1 = f.Value<string>();
                    */
                Assert.Equal(sessionContext1, v1);
            }
            /*
            JObject obj = null;
            try
            {
                obj = JObject.Parse(json);
            } catch(Exception ex)
            {
                Assert.True(false, json + " cannot be parsed: " + ex);
            }
            try
            {
                var token = obj.SelectTokens("..v1");
                if(token is JValue)
                    Assert.Equal(sessionContext1, token.Value<string>());
                else
                    Assert.Equal(sessionContext1, token.Values<string>().First());
            } catch(Exception ex)
            {
                throw;
            }
            */
            //Assert.Equal(sessionContext2, obj.SelectToken("..v2").First.Value<string>());
        }

        [Theory, PairwiseData]
        public async Task ReturnsXml(  [CombinatorialValues("stream", "writer","mapper", "command")] string client, 
                                       [CombinatorialValues(1, 5, 500, 1000)] string top,
                                       [CombinatorialValues("auto", "path", "raw")] string mode,
                                       [CombinatorialValues("", "test")] string rootmode,
                                       [CombinatorialValues(true, false)] bool useCommand)
        {
            // Arrange
            string sql = "select top " + top + " 'CONST_ID' as const, o.object_id tid, o.name t_name, o.schema_id, o.type t, o.type_desc, o.create_date cd, o.modify_date md from sys.objects o for xml " + mode + ", root" + (rootmode!=""?"('"+rootmode+"')":"");
            var xml = new XmlDocument();

            // Action
            if (client == "stream")
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    if (useCommand)
                        await pipe.Stream(new SqlCommand(sql), ms);
                    else
                        await pipe.Stream(sql, ms);
                    ms.Position = 0;
                    xml.Load(ms);   
                }
            } else if (client == "writer")
            {
                using (var sw = new StringWriter())
                {
                    if(useCommand)
                        await pipe.Stream(new SqlCommand(sql), sw, "<root/>");
                    else
                        await pipe.Stream(sql, sw, "<root/>");
                    xml.LoadXml(sw.ToString());
                }
            } else if (client == "command")
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    if (useCommand)
                        await command.Stream(new SqlCommand(sql), ms, "<root/>");
                    else
                        await command.Stream(sql, ms, "<root/>");
                    ms.Position = 0;
                    xml.LoadXml( new StreamReader(ms).ReadToEnd() );
                }
            } else
            {
                if (useCommand)
                    xml.LoadXml(await mapper.GetStringAsync(new SqlCommand(sql)));
                else
                    xml.LoadXml(await mapper.GetStringAsync(sql));
            }

            // Assert

            //-- auto, root             root / o / @const
            //-- auto, root('test')     test / o / @const
            //-- raw, root('test')      test / row / @const
            //-- raw, root              root / row / @const
            //-- path, root             root / row /const
            //-- path, root('test')     test / row /const

            string prefix = (rootmode=="")?"root":"test";
            if (mode == "auto")
                prefix += "/o/";
            else
                prefix += "/row/";

            if (mode == "raw" || mode == "auto")
                prefix += "@";

            Assert.True(xml.ChildNodes[0].ChildNodes.Count > 0);
            Assert.Equal("CONST_ID", xml.SelectSingleNode("//" + prefix + "const").InnerText);
            Assert.NotEmpty(xml.SelectSingleNode("//" + prefix + "tid").InnerText);
        }

        [Theory, PairwiseData]
        public async Task ReturnsDefaultValue(
                [CombinatorialValues(0, 1, 50, 10000)] int length,
                [CombinatorialValues(false, true)] bool STRING,
                [CombinatorialValues("[/]", "{\"a\":/}", "/")] string wrapper,
                [CombinatorialValues("writer", "stream", "command-stream")] string client,
                [CombinatorialValues(true, false)] bool useCommand,
                [CombinatorialValues(true, false)] bool useAsync)
        {
            // Arrange
            var sql = "select * from sys.all_objects where 1 = 0";
            string defaultValue = length==0 ? "" : GenerateChar(length), text = "INITIAL_VALUE";
            var pair = wrapper.Split('/');
            string prefix = pair[0];
            string suffix = pair[1];
            Task t = null;

            // Action
            if (client == "stream")
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    if (length == 0)
                    {
                        if (useCommand)
                            t = pipe.Stream(new SqlCommand(sql), ms, new Options() { DefaultOutput = defaultValue, Prefix = prefix, Suffix = suffix });
                        else
                            t = pipe.Stream(sql, ms, new Options() { DefaultOutput = defaultValue, Prefix = prefix, Suffix = suffix });
                    }
                    else if (STRING)
                    {
                        if (useCommand)
                            t = pipe.Stream(new SqlCommand(sql), ms, new Options() { DefaultOutput = defaultValue, Prefix = prefix, Suffix = suffix });
                        else
                            t = pipe.Stream(sql, ms, new Options() { DefaultOutput = defaultValue, Prefix = prefix, Suffix = suffix });
                    }
                    else
                    {
                        if (useCommand)
                            t = pipe.Stream(new SqlCommand(sql), ms, new Options() { DefaultOutput = Encoding.UTF8.GetBytes(defaultValue), Prefix = prefix, Suffix = suffix });
                        else
                            t = pipe.Stream(sql, ms, new Options() { DefaultOutput = Encoding.UTF8.GetBytes(defaultValue), Prefix = prefix, Suffix = suffix });
                    }

                    if (useAsync)
                        await t;
                    else
                        t.Wait();

                    ms.Position = 0;
                    text = new StreamReader(ms).ReadToEnd();
                }
            }
            else if (client == "writer")
            {
                using (var ms = new StringWriter())
                {
                    if (length == 0)
                    {
                        if (useCommand)
                            t = pipe.Stream(new SqlCommand(sql), ms, new Options() { DefaultOutput = "", Prefix = prefix, Suffix = suffix });
                        else
                            t = pipe.Stream(sql, ms, new Options() { DefaultOutput = "", Prefix = prefix, Suffix = suffix });
                    }
                    else if (STRING)
                    {
                        if (useCommand)
                            t = pipe.Stream(new SqlCommand(sql), ms, new Options() { DefaultOutput = defaultValue, Prefix = prefix, Suffix = suffix });
                        else
                            t = pipe.Stream(sql, ms, new Options() { DefaultOutput = defaultValue, Prefix = prefix, Suffix = suffix });
                    }
                    else
                    {
                        // cannot send binary default value to TextWriter.
                        // Invalid test case => abort test
                        return;
                    }

                    if (useAsync)
                        await t;
                    else
                        t.Wait();

                    text = ms.ToString();
                }
            }
            else if (client == "command-stream")
            {
                using (var ms = new MemoryStream())
                {
                    if (length == 0)
                    {
                        if (useCommand)
                            t = command.Stream(new SqlCommand(sql), ms, new Options() { DefaultOutput = "", Prefix = prefix, Suffix = suffix });
                        else
                            t = command.Stream(sql, ms, new Options() { DefaultOutput = "", Prefix = prefix, Suffix = suffix });
                    }
                    else if (STRING)
                    {
                        if (useCommand)
                            t = command.Stream(new SqlCommand(sql), ms, new Options() { DefaultOutput = defaultValue, Prefix = prefix, Suffix = suffix });
                        else
                            t = command.Stream(sql, ms, new Options() { DefaultOutput = defaultValue, Prefix = prefix, Suffix = suffix });
                    }
                    else
                    {
                        if (useCommand)
                            t = command.Stream(new SqlCommand(sql), ms, new Options() { DefaultOutput = Encoding.UTF8.GetBytes(defaultValue), Prefix = prefix, Suffix = suffix });
                        else
                            t = command.Stream(sql, ms, new Options() { DefaultOutput = Encoding.UTF8.GetBytes(defaultValue), Prefix = prefix, Suffix = suffix });
                    }

                    if (useAsync)
                        await t;
                    else
                        t.Wait();

                    ms.Position = 0;
                    text = new StreamReader(ms).ReadToEnd();
                }
            }

            // Assert
            Assert.Equal(prefix + defaultValue + suffix, text);
        }

        public string GenerateChar()
        {
            Random random = new Random();

            return Convert.ToChar(Convert.ToInt32(Math.Floor(26 * random.NextDouble() + 65))).ToString();
        }

        public string GenerateChar(int count)
        {
            string randomString = "";

            for (int i = 0; i < count; i++)
            {
                randomString += GenerateChar();
            }

            return randomString;
        }

    }
}