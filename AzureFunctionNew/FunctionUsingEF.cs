using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Newtonsoft.Json;

namespace AzureFunctionNew
{
    public class Hierarchy
    {
        public string MainFacility { get; set; }
        public string ProcessUnit { get; set; }
        public string Tag { get; set; }
    }
    public static class FunctionUsingEF
    {
        [FunctionName("FunctionUsingEF")]
        public static async Task<HttpResponseMessage> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequest req, ILogger log, ClaimsPrincipal cp, ExecutionContext context)
        {
            var config = new ConfigurationBuilder()
            .SetBasePath(context.FunctionAppDirectory)
            .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build(); log.LogInformation("C# HTTP trigger function processed a request.");

            // To read the AAD group in the request
            //string ipGroups = req.Query["aadGroups"];
            //string ipGroups = "AZ-DNA-US-01-SQDW-AIF-DEVELOPER";// req.Query["aadGroups"];
            string env = req.Query["env"];
            string ipGroups = req.Query["aadGroups"];
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);
            env = env ?? data?.env;
            ipGroups = ipGroups ?? data?.aadGroups;

            if (string.IsNullOrEmpty(env))
            {
                return makeHttpErrorResponse(System.Net.HttpStatusCode.BadRequest, "Please provide a value for the enviornment parameter");
            }
            if (string.IsNullOrEmpty(ipGroups))
            {
                return makeHttpErrorResponse(System.Net.HttpStatusCode.BadRequest, "Please provide a value for the `aadGroups` parameter");
            }
            //string currentUser = cp.Identity.Name;
            ipGroups = ipGroups.TrimStart('\'').TrimEnd('\'');
            List<String> groupNames = new List<String>(ipGroups.Split(';'));

            var client = GetGraphApiClient(log, config["AAD_TENANT"]).Result;
            if (client != null) { log.LogInformation($"Received client"); }



            List<AADGroup> aadGroups = new List<AADGroup>();
            List<AADGroupMember> otherMembers = new List<AADGroupMember>();
            List<AADUser> users = new List<AADUser>();

            List<AADServicePrincipal> servicePrincipals = new List<AADServicePrincipal>();
            Dictionary<string, string> fileNames = new Dictionary<string, string>();

            foreach (string groupName in groupNames)
            {

                try
                {
                    IGraphServiceGroupsCollectionPage groups = await
                                   client.Groups.Request()
                                   .Filter($"DisplayName eq '{groupName}'")
                                   .Expand("Members")
                                   .GetAsync();


                    if (groups?.Count > 0)
                    {
                        foreach (Group group in groups)
                        {
                            AADGroup aadGroup = new AADGroup(group.Id, group.DisplayName);
                            log.LogInformation($"Processing '{group.DisplayName}'");

                            var groupUsers = await client.Groups[group.Id].Members.Request().GetAsync();
                            do
                            {
                                foreach (User user in groupUsers)
                                {
                                    users.Add(new AADUser(group, user.Id, "User", user.DisplayName, user.UserPrincipalName));
                                }
                            }
                            while (groupUsers.NextPageRequest != null && (groupUsers = await groupUsers.NextPageRequest.GetAsync()).Count > 0);
                            aadGroups.Add(aadGroup);
                        }
                    }
                }
                catch (Exception e)
                {

                    throw;
                }
            }

            var connectionString = config["AzureDBModelEntities"];
            string accessToken = await getAADToken("https://database.windows.net/", config["AAD_TENANT"]);
            log.LogInformation(accessToken);
            int businessAreas = 0;
            SqlConnection conn = new SqlConnection();
            conn.AccessToken = accessToken;
            GetDatabaseConnection(env.ToUpper(), conn);
            try
            {
                //conn.ConnectionString = "Data Source=shell-01-eun-sq-nfowhbtpuquawrbuwtjv.database.windows.net; Initial Catalog = shell-01-eun-sqdb-qddsheofnywfaatpszdy;Persist Security Info=False;User ID=DATA_BRICKS;Password=DB@12345;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;";

                conn.Open();
                //string queryString = "select count(*) from DM_MDM.DIM_BUSINESS_AREA";
                // Create the Command and Parameter objects.
                //SqlCommand command = new SqlCommand(queryString, conn);
                //businessAreas = (int)command.ExecuteScalar();
                if (aadGroups.Count > 0)
                {
                    //Truncate staging table
                    string truncateQuery = "TRUNCATE TABLE [STAGE].[AAD_GROUP]";
                    SqlCommand truncCommand = new SqlCommand(truncateQuery, conn);
                    truncCommand.ExecuteNonQuery();
                    foreach (AADGroup group in aadGroups)
                    {
                        InsertAADGroup(conn, group.Id, group.DisplayName);
                    }
                    //Commented as merge will be handled in ADF pipeline
                    //using (var spCommand = new SqlCommand("DM_SECURITY.USP_POPULATE_AAD_GROUP", conn)
                    //{
                    //    CommandType = CommandType.StoredProcedure
                    //})
                    //{
                    //    spCommand.ExecuteNonQuery();
                    //}
                }
                if (users.Count > 0)
                {
                    //Truncate staging table
                    string truncateQuery = "TRUNCATE TABLE [STAGE].[AAD_USER]";
                    SqlCommand truncCommand = new SqlCommand(truncateQuery, conn);
                    truncCommand.ExecuteNonQuery();
                    foreach (AADUser user in users)
                    {
                        InsertUser(conn, user.groupId, user.groupName, user.id, user.memberType, user.displayName, user.userPrincipalName);
                    }
                    //Commented as merge will be handled in ADF
                    //using (var spCommand = new SqlCommand("DM_SECURITY.USP_POPULATE_AAD_USER", conn)
                    //{
                    //    CommandType = CommandType.StoredProcedure
                    //})
                    //{
                    //    spCommand.ExecuteNonQuery();
                    //}
                }
            }
            catch (Exception e)
            {
                log.LogInformation(e.Message.ToString());
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent(e.Message.ToString())
                };
            }
            finally
            {
                if (conn != null)
                {
                    conn.Close();
                }

            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("Users Count is " + users.Count.ToString() + "and active directory group count is" + aadGroups.Count.ToString())
            };
        }

        public static async Task<String> getAADToken(string resource, string aad)
        {
            var azureServiceTokenProvider = new AzureServiceTokenProvider();
            return await azureServiceTokenProvider.GetAccessTokenAsync(resource, aad);
        }

        private static HttpResponseMessage makeHttpErrorResponse(System.Net.HttpStatusCode code, string message)
        {
            return new HttpResponseMessage(code)
            {
                Content = new StringContent(message, Encoding.UTF8, "application/json")
            };
        }

        private static void GetDatabaseConnection(string env, SqlConnection conn)
        {
            switch (env)
            {
                case "DEV":
                    conn.ConnectionString = "Data Source=shell-01-eun-sq-nfowhbtpuquawrbuwtjv.database.windows.net,1433;Initial Catalog = shell-01-eun-sqdb-qddsheofnywfaatpszdy";
                    break;
                case "TEST":
                    conn.ConnectionString = "Data Source = shell-01-eun-sq-vzwfwiuxeqooihdnmzrr.database.windows.net,1433; Initial Catalog = shell-01-eun-sqdb-kzaqalxoemscvqwufbja";
                    break;
                case "ACPT":
                    conn.ConnectionString = "Data Source = shell-01-eun-sq-bqteoyhtlgmrsqkgrihh.database.windows.net,1433; Initial Catalog = shell-01-eun-sqdb-qjpsrawkmekixnuqbfvn";
                    break;
                case "PROD":
                    conn.ConnectionString = "Data Source = shell-31-eun-sq-chsnpymewtyckcldibpx.database.windows.net,1433; Initial Catalog = shell-31-eun-sqdb-qqhihawjzndeawoyfkuq";
                    break;
                default:
                    // conn.ConnectionString = "Data Source = shell-01-eun-sq-ykhsuckvfssfigkdykyk.database.windows.net,1433; Initial Catalog = shell-01-eun-sqdb-gmzhtnzqeveykxryoccp";
                    break;
            }
        }
        private static async Task<GraphServiceClient> GetGraphApiClient(ILogger log, string aad)
        {

            var azureServiceTokenProvider = new AzureServiceTokenProvider();

            string accessToken = await azureServiceTokenProvider
                .GetAccessTokenAsync("https://graph.microsoft.com/", aad);


            var graphServiceClient = new GraphServiceClient(
                GraphClientFactory.Create(
                    new DelegateAuthenticationProvider((requestMessage) =>
                    {
                        requestMessage
                    .Headers
                    .Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("bearer", accessToken);

                        return Task.CompletedTask;
                    }))
                );

            return graphServiceClient;
        }

        private static void InsertUser(SqlConnection conn, string groupId, string groupDisplayName, string id, string memberType, string userName, string principalName)
        {

            // define INSERT query with parameters
            string query = "INSERT INTO [STAGE].[AAD_USER]([AAD_GROUP_ID],[GROUP_DISPLAY_NAME],[ID],[MEMBER_TYPE],[USER_DISPLAY_NAME],[USER_PRINCIPAL_NAME],[CREATION_USER_ID],[CREATION_DATE],[LAST_UPDATE_USER_ID],[LAST_UPDATE_DATE])" +
                "VALUES (@groupId, @groupDisplayName, @id, @memberType, @userName, @principalName," + "'PL_AAD_USER_MAPPING'" + ",'" + System.DateTime.Now + "'," + "'PL_AAD_USER_MAPPING'" + ",'" + System.DateTime.Now + "')";

            // create connection and command
            using (SqlCommand cmd = new SqlCommand(query, conn))
            {
                // define parameters and their values
                cmd.Parameters.Add("@groupId", SqlDbType.VarChar, 255).Value = groupId;
                cmd.Parameters.Add("@groupDisplayName", SqlDbType.VarChar, 255).Value = groupDisplayName;
                cmd.Parameters.Add("@id", SqlDbType.VarChar, 255).Value = id;
                cmd.Parameters.Add("@memberType", SqlDbType.VarChar, 255).Value = memberType;
                cmd.Parameters.Add("@userName", SqlDbType.VarChar, 255).Value = userName;
                cmd.Parameters.Add("@principalName", SqlDbType.VarChar, 255).Value = principalName;

                cmd.ExecuteNonQuery();
            }
        }

        private static void InsertAADGroup(SqlConnection conn, string Id, string groupDisplayName)
        {

            // define INSERT query with parameters
            string query = "INSERT INTO [STAGE].[AAD_GROUP]([ID],[GROUP_DISPLAY_NAME],[CREATION_USER_ID],[CREATION_DATE],[LAST_UPDATE_USER_ID],[LAST_UPDATE_DATE])" +
                "VALUES (@groupId, @groupDisplayName," + "'PL_AAD_USER_MAPPING'" + ",'" + System.DateTime.Now + "'," + "'PL_AAD_USER_MAPPING'" + ",'" + System.DateTime.Now + "')";

            // create connection and command
            using (SqlCommand cmd = new SqlCommand(query, conn))
            {
                // define parameters and their values
                cmd.Parameters.Add("@groupId", SqlDbType.VarChar, 255).Value = Id;
                cmd.Parameters.Add("@groupDisplayName", SqlDbType.VarChar, 255).Value = groupDisplayName;

                cmd.ExecuteNonQuery();
            }
        }

        private static void InsertSPN(SqlConnection conn, string groupId, string groupDisplayName, string id, string memberType, string spnDisplayName, string applicationID)
        {
            // define INSERT query with parameters
            string query = "INSERT INTO[DM_SECURITY].[AAD_USER]([AAD_SERVICE_PRINCIPAL],[GROUP_DISPLAY_NAME],[ID],[MEMBER_TYPE],[SPN_DISPLAY_NAME],[APPLICATION_ID],[CREATION_ID],[CREATION_DATETIME],[LAST_MODIFIED_ID],[LAST_MODIFIED_DATETIME])" +
                "VALUES (@groupId, @groupDisplayName, @id, @memberType, @spnName, @applicationId," + "'PL_AAD_USER_MAPPING'" + "," + System.DateTime.Now + "," + "'PL_AAD_USER_MAPPING'" + "," + System.DateTime.Now + ")";


            SqlCommand cmda;
            SqlDataAdapter da = new SqlDataAdapter("SELECT * FROM [DM_SECURITY].[AAD_SERVICE_PRINCIPAL] WHERE AAD_GROUP_ID = '" + groupId + "' AND ID = '" + id + "'", conn);
            DataSet ds = new DataSet();
            da.Fill(ds);

            if (ds.Tables[0].Rows.Count > 0)
            {

                cmda = new SqlCommand("UPDATE [DM_SECURITY].[AAD_SERVICE_PRINCIPAL] SET GROUP_DISPLAY_NAME='" + groupDisplayName + "' ,SPN_DISPLAY_NAME='" + spnDisplayName + "' ,LAST_MODIFIED_ID ='PL_AAD_USER_MAPPING',LAST_MODIFIED_DATETIME='" + System.DateTime.Now + "' WHERE AAD_GROUP_ID = '" + groupId + "' AND ID = '" + id + "'", conn);
                cmda.ExecuteNonQuery();
            }
            else
            {
                // create connection and command
                using (SqlCommand cmd = new SqlCommand(query, conn))
                {
                    // define parameters and their values
                    cmd.Parameters.Add("@groupId", SqlDbType.VarChar, 255).Value = groupId;
                    cmd.Parameters.Add("@groupDisplayName", SqlDbType.VarChar, 255).Value = groupDisplayName;
                    cmd.Parameters.Add("@id", SqlDbType.VarChar, 255).Value = id;
                    cmd.Parameters.Add("@memberType", SqlDbType.VarChar, 255).Value = memberType;
                    cmd.Parameters.Add("@spnName", SqlDbType.VarChar, 255).Value = spnDisplayName;
                    cmd.Parameters.Add("@applicationId", SqlDbType.VarChar, 255).Value = applicationID;

                    // open connection, execute INSERT, close connection
                    //cn.Open();
                    cmd.ExecuteNonQuery();
                    //cn.Close();
                }
            }
        }

        private static void InsertOtherMember(SqlConnection conn, string groupId, string groupDisplayName, string id, string memberType)
        {
            // define INSERT query with parameters
            string query = "INSERT INTO[DM_SECURITY].[AAD_GROUP_MEMBER]([AAD_GROUP_ID],[GROUP_DISPLAY_NAME],[ID],[MEMBER_TYPE],[CREATION_ID],[CREATION_DATETIME],[LAST_MODIFIED_ID],[LAST_MODIFIED_DATETIME])" +
                "VALUES (@groupId, @groupDisplayName, @id, @memberType, @spnName, @applicationId," + "'PL_AAD_USER_MAPPING'" + "," + System.DateTime.Now + "," + "'PL_AAD_USER_MAPPING'" + "," + System.DateTime.Now + ")";

            SqlCommand cmda;
            SqlDataAdapter da = new SqlDataAdapter("SELECT * FROM [DM_SECURITY].[AAD_GROUP_MEMBER] WHERE AAD_GROUP_ID = '" + groupId + "' AND ID = '" + id + "'", conn);
            DataSet ds = new DataSet();
            da.Fill(ds);

            if (ds.Tables[0].Rows.Count > 0)
            {

                cmda = new SqlCommand("UPDATE [DM_SECURITY].[AAD_GROUP_MEMBER] SET GROUP_DISPLAY_NAME='" + groupDisplayName + "' ,MEMBER_TYPE='" + memberType + "' ,LAST_MODIFIED_ID ='PL_AAD_USER_MAPPING',LAST_MODIFIED_DATETIME='" + System.DateTime.Now + "' WHERE AAD_GROUP_ID = '" + groupId + "' AND ID = '" + id + "'", conn);
                cmda.ExecuteNonQuery();
            }
            else
            {
                // create connection and command
                using (SqlCommand cmd = new SqlCommand(query, conn))
                {
                    // define parameters and their values
                    cmd.Parameters.Add("@groupId", SqlDbType.VarChar, 255).Value = groupId;
                    cmd.Parameters.Add("@groupDisplayName", SqlDbType.VarChar, 255).Value = groupDisplayName;
                    cmd.Parameters.Add("@id", SqlDbType.VarChar, 255).Value = id;
                    cmd.Parameters.Add("@memberType", SqlDbType.VarChar, 255).Value = memberType;

                    // open connection, execute INSERT, close connection
                    //cn.Open();
                    cmd.ExecuteNonQuery();
                    //cn.Close();
                }
            }
        }

    }
}

