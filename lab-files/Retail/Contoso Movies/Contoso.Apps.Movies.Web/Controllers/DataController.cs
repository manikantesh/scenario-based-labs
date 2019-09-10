﻿using Contoso.Apps.Movies.Data.Models;
using Contoso.Apps.Movies.Logic;
using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Client;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Web;
using System.Web.Http;
using System.Web.Http.Cors;
using System.Web.Http.Results;

namespace Contoso.Apps.Movies.Web.Controllers
{
    [AllowAnonymous]
    [EnableCors(origins: "*", headers: "*", methods: "*")]
    public class DataController : ApiController
    {
        protected DocumentClient client;
        protected Database database;
        protected string databaseId;
        protected DocumentCollection productColl, shoppingCartItems;

        protected static readonly FeedOptions DefaultOptions = new FeedOptions { EnableCrossPartitionQuery = true };

        public DataController()
        {
            string endpointUrl = ConfigurationManager.AppSettings["dbConnectionUrl"];
            string authorizationKey = ConfigurationManager.AppSettings["dbConnectionKey"];
            databaseId = ConfigurationManager.AppSettings["databaseId"];

            client = new DocumentClient(new Uri(endpointUrl), authorizationKey, new ConnectionPolicy { ConnectionMode = ConnectionMode.Gateway, ConnectionProtocol = Protocol.Https });
            database = client.CreateDatabaseIfNotExistsAsync(new Database { Id = databaseId }).Result;
        }

        [HttpGet]
        [Route("api/logs")]
        public IHttpActionResult Logs()
        {
            List<CollectorLog> logs = new List<CollectorLog>();

            Contoso.Apps.Movies.Data.Models.User user = (Contoso.Apps.Movies.Data.Models.User)HttpContext.Current.Session["User"];
            string name = user.Email;
            int userId = user.UserId;

            Uri collectionUri = UriFactory.CreateDocumentCollectionUri(databaseId, "collector_log");

            logs = client.CreateDocumentQuery<CollectorLog>(collectionUri,
                new SqlQuerySpec(
                    "SELECT * FROM collector_log r WHERE r.UserId = @userid",
                    new SqlParameterCollection(new[]
                    {
                        new SqlParameter { Name = "@userid", Value = userId }
                    }
                    )
                    ), DefaultOptions
            ).ToList();

            return Json(logs);
        }

        [HttpGet]
        [Route("api/similar")]
        public IHttpActionResult SimilarUsers(string algo)
        {
            List<Data.Models.User> users = new List<Data.Models.User>();

            Contoso.Apps.Movies.Data.Models.User user = (Contoso.Apps.Movies.Data.Models.User)HttpContext.Current.Session["User"];
            string name = user.Email;
            int userId = user.UserId;

            switch (algo)
            {
                case "jaccard":
                    users = RecommendationHelper.JaccardRecommendation(userId);
                    break;
                case "pearson":
                    users = RecommendationHelper.PearsonRecommendation(userId);
                    break;
            }
            
            return Json(users);
        }

        [HttpGet]
        [Route("api/recommend")]
        public IHttpActionResult Recommend(string algo, int count)
        {
            List<Item> products = new List<Item>();

            Contoso.Apps.Movies.Data.Models.User user = (Contoso.Apps.Movies.Data.Models.User)HttpContext.Current.Session["User"];
            string name = user.Email;
            int userId = user.UserId;

            products = RecommendationHelper.Get(algo, userId, count);

            return Json(products);
        }
    }
}