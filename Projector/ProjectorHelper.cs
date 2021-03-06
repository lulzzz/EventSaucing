﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using Dapper;
using EventSaucing.Storage;
using Scalesque;

namespace EventSaucing.Projector {
    public static class ProjectorHelper {
        /// <summary>
        ///     Gets the uniqueprojectorId of a projector
        /// </summary>
        /// <param name="projector"></param>
        /// <exception cref="ArgumentException">Thrown if the attribute is mssing</exception>
        /// <returns></returns>
        public static int GetProjectorId(this ProjectorBase projector) {
            return GetProjectorId(projector.GetType());
        }

        /// <summary>
        ///     Gets the uniqueprojectorId of a projector
        /// </summary>
        /// <exception cref="ArgumentException">Thrown if the attribute is mssing</exception>
        /// <returns></returns>
        public static int GetProjectorId(this Type projectorType) {
            return projectorType.GetCustomAttributes(false)
                                .FlatMap(x => x.To<ProjectorAttribute>())
                                .HeadOption()
                                .Map(x => x.ProjectorId)
                                .GetOrElse(
                                    () => {
                                        throw new ArgumentException("projector doesn't have the ProjectorAttribute");
                                    });
        }

        /// <summary>
        ///     NEventStore uses checkpoint tokens typed as strings
        /// </summary>
        /// <param name="checkpoint"></param>
        /// <returns></returns>
        public static string ToCheckpointToken(this Option<long> checkpoint) => checkpoint.Map(x => x.ToString()).GetOrElse(() => null);


        const string SqlPersistProjectorState = @"
			MERGE dbo.ProjectorStatus AS target
			USING (SELECT @ProjectorId, @ProjectorName, @LastCheckpointToken) AS source (ProjectorId, ProjectorName, LastCheckpointToken)
			ON (target.ProjectorId = source.ProjectorId)
			WHEN MATCHED THEN 
				UPDATE SET LastCheckpointToken = source.LastCheckpointToken
			WHEN NOT MATCHED THEN	
				INSERT (ProjectorId, ProjectorName, LastCheckpointToken)
				VALUES (source.ProjectorId, source.ProjectorName, source.LastCheckpointToken);";

        /// <summary>
        /// Persists the projector's current checkpoint in the db (in scope of tx)
        /// </summary>
        /// <param name="projector"></param>
        /// <param name="tx"></param>
        public static void PersistProjectorCheckpoint(this ProjectorBase projector, IDbTransaction tx) {
            var sqlParams = GetProjectorParams(projector);
            tx.Connection.Execute(SqlPersistProjectorState, sqlParams, tx);
        }

		const string SqlInitialiseProjectorStatus = @"
			IF (NOT EXISTS (SELECT * 
                 FROM INFORMATION_SCHEMA.TABLES 
                 WHERE TABLE_SCHEMA = 'dbo' 
                 AND TABLE_NAME = 'ProjectorStatus'))
			BEGIN
				CREATE TABLE [dbo].[ProjectorStatus](
					[ProjectorId] [int] NOT NULL,
					[ProjectorName] [nvarchar](800) NOT NULL,
					[LastCheckpointToken] [bigint] NULL,
					CONSTRAINT [PK_ProjectorStatus] PRIMARY KEY CLUSTERED 
					(
						[ProjectorId] ASC
					) ON [PRIMARY]
				) ON [PRIMARY]
			END";

		public static void InitialiseProjectorStatusStore(IDbService dbService) {
			//get the head checkpoint (if there is one)
			using (var conn = dbService.GetConnection()) {
				conn.Open();
				conn.Execute(SqlInitialiseProjectorStatus);
			}
		}

        public static List<Type> FindAllProjectorsInProject() {
            //Reflect on assembly to identify projectors and have DI create them
	        var types = Assembly.GetEntryAssembly().GetTypes();
            return
                (from type in types
				 where type.GetCustomAttributes(typeof (ProjectorAttribute), false).Any()
                    select type
                    ).ToList();
        }

        private static object GetProjectorParams(ProjectorBase projector) {
            return new {
                ProjectorId = projector.ProjectorId,
                ProjectorName = projector.GetType().Name,
                LastCheckpointToken = projector.Checkpoint.Get()
            };
        }

        /// <summary>
        /// Persists the projector's current checkpoint in the db (no tx)
        /// </summary>
        /// <param name="projector"></param>
        /// <param name="conn"></param>
        public static void PersistProjectorCheckpoint(this ProjectorBase projector, IDbConnection conn) {
            var sqlParams = GetProjectorParams(projector);
            conn.Execute(SqlPersistProjectorState, sqlParams);
        }
    }
}