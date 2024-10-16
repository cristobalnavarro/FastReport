﻿using FastReport.Data;
using FastReport.Utils;
using FastReport.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;

namespace FastReport.Web.Services
{
    internal sealed class ConnectionService : IConnectionsService
    {
        private static Type GetConnectionType(string connectionType)
        {
            var objects = new List<DataConnectionInfo>();
            RegisteredObjects.DataConnections.EnumItems(objects);

            foreach (var info in objects)
                if (info.Object != null && info.Object.FullName == connectionType)
                {
                    return info.Object;
                }
            return null;
        }

        public string GetConnectionStringPropertiesJSON(string connectionType, string connectionString, out bool isError)
        {
            Type connType = GetConnectionType(connectionType);

            if (connType == null)
            {
                isError = true;
                return "Connection type not found";
            }

            var data = new StringBuilder();

            // this piece of code mimics functionality of PropertyGrid: finds available properties
            try
            {
                using (var conn = (DataConnectionBase)Activator.CreateInstance(connType))
                {
                    conn.ConnectionString = connectionString;
                    PropertyDescriptorCollection props = TypeDescriptor.GetProperties(conn);

                    foreach (PropertyDescriptor pd in props)
                    {
                        if (!pd.IsBrowsable || pd.IsReadOnly)
                            continue;

                        if (pd.Name == "Name" ||
                            pd.Name == "ConnectionString" ||
                            pd.Name == "ConnectionStringExpression" ||
                            pd.Name == "LoginPrompt" ||
                            pd.Name == "CommandTimeout" ||
                            pd.Name == "Alias" ||
                            pd.Name == "Description" ||
                            pd.Name == "Restrictions")
                            continue;

                        object value = null;

                        try
                        {
                            object owner = conn;
                            if (conn is ICustomTypeDescriptor customTypeDescriptor)
                                owner = customTypeDescriptor.GetPropertyOwner(pd);
                            value = pd.GetValue(owner);
                        }
                        catch { }

                        data.Append("{");
                        data.Append("\"name\":\"" + JavaScriptEncoder.Default.Encode(pd.Name) + "\",");
                        data.Append("\"displayName\":\"" + JavaScriptEncoder.Default.Encode(pd.DisplayName) + "\",");
                        data.Append("\"description\":\"" + JavaScriptEncoder.Default.Encode(pd.Description) + "\",");
                        data.Append("\"value\":\"" + JavaScriptEncoder.Default.Encode(value == null ? "" : value.ToString()) + "\",");
                        data.Append("\"propertyType\":\"" + JavaScriptEncoder.Default.Encode(pd.PropertyType.FullName) + "\"");
                        data.Append("},");
                    }
                }
            }
            catch (Exception ex)
            {
                isError = true;
                return ex.ToString();
            }

            isError = false;
            return $@"{{""properties"":[{data.ToString().TrimEnd(',')}]}}";
        }

        public string CreateConnectionStringJSON(string connectionType, IFormCollection form, out bool isError)
        {
            Type connType = GetConnectionType(connectionType);

            if (connType == null)
            {
                isError = true;
                return "Connection type not found";
            }

            try
            {
                using (var conn = (DataConnectionBase)Activator.CreateInstance(connType))
                {
                    PropertyDescriptorCollection props = TypeDescriptor.GetProperties(conn);

                    foreach (PropertyDescriptor pd in props)
                    {
                        if (!pd.IsBrowsable || pd.IsReadOnly)
                            continue;

                        if (pd.Name == "Name" ||
                            pd.Name == "ConnectionString" ||
                            pd.Name == "ConnectionStringExpression" ||
                            pd.Name == "LoginPrompt" ||
                            pd.Name == "CommandTimeout" ||
                            pd.Name == "Alias" ||
                            pd.Name == "Description" ||
                            pd.Name == "Restrictions")
                            continue;

                        try
                        {
                            string propertyValue = form[pd.Name].ToString();
                            TypeConverter typeConverter = TypeDescriptor.GetConverter(pd.PropertyType);
                            object value = typeConverter.ConvertFromString(propertyValue);

                            object owner = conn;
                            if (conn is ICustomTypeDescriptor customTypeDescriptor)
                                owner = customTypeDescriptor.GetPropertyOwner(pd);
                            pd.SetValue(owner, value);
                        }
                        catch (Exception ex)
                        {
                            isError = true;
                            return ex.ToString();
                        }
                    }

                    isError = false;
                    return $@"{{""connectionString"":""{JavaScriptEncoder.Default.Encode(conn.ConnectionString)}""}}";
                }
            }
            catch (Exception ex)
            {
                isError = true;
                return ex.ToString();
            }
        }

        public string GetConnectionTables(string connectionType, string connectionString, List<CustomViewModel> customViews)
        {
            if (!IsConnectionStringValid(connectionString, out var errorMsg))
                throw new Exception(errorMsg);

            try
            {
                using var conn = CreateConnection(connectionType);

                conn.ConnectionString = connectionString;

                if (FastReportGlobal.AllowCustomSqlQueries)
                {
                    foreach (var view in customViews)
                    {
                        var source = new TableDataSource
                        {
                            Table = new DataTable(),
                            TableName = view.TableName,
                            Name = view.TableName,
                            SelectCommand = view.SqlQuery
                        };

                        conn.Tables.Add(source);
                        conn.DataSet.Tables.Add(source.Table);
                    }
                }

                conn.CreateAllTables(true);

                foreach (TableDataSource c in conn.Tables)
                    c.Enabled = true;

                return SerializeToString(conn);
            }
            catch 
            {
                throw new Exception("Error in creating tables. Please verify your connection string.");
            }
        }

        public string GetUpdatedTableByReportId(WebReport webReport, UpdateTableParams parameters)
        {
            var dataSource = webReport.Report.GetDataSource(parameters.TableName) as TableDataSource
                             ?? throw new Exception("Table not found");

            try
            {
                foreach (var parameter in parameters.Parameters)
                {
                    ApplyParameterToDataSource(dataSource, parameter);
                }

                dataSource.SelectCommand = parameters.SqlQuery;
                dataSource.RefreshTable();

                return SerializeToString(dataSource);
            }
            catch (Exception ex)
            {
                throw new Exception("Error in creating tables. Please verify your parameters.", ex);
            }
        }

        public string GetUpdatedTableByConnectionString(string connectionString, string connectionType,
            UpdateTableParams parameters)
        {
            if (!IsConnectionStringValid(connectionString, out var errorMsg))
                throw new ArgumentException(errorMsg);

            try
            {
                using var conn = CreateConnection(connectionType);
                conn.ConnectionString = connectionString;
                conn.CreateAllTables(true);

                var dataSource = conn.Tables.Cast<TableDataSource>()
                                     .FirstOrDefault(table => string.Equals(table.TableName, parameters.TableName))
                                 ?? throw new ArgumentException("Table not found");

                foreach (var parameter in parameters.Parameters)
                {
                    ApplyParameterToDataSource(dataSource, parameter);
                }

                dataSource.SelectCommand = parameters.SqlQuery;
                dataSource.RefreshTable();

                return SerializeToString(dataSource);
            }
            catch (Exception ex)
            {
                throw new Exception("Error updating table in the database.", ex);
            }
        }

        private static CommandParameter CreateParameter(ParameterModel parameterParams)
        {
            return new CommandParameter
            {
                Name = parameterParams.Name ?? string.Empty,
                Value = parameterParams.Value ?? string.Empty,
                DataType = parameterParams.DataType,
                Expression = parameterParams.Expression ?? string.Empty
            };
        }

        private static DataConnectionBase CreateConnection(string connectionType)
        {
            var connType = GetConnectionType(connectionType);

            return connType == null
                ? throw new ArgumentException("Connection type not found")
                : (DataConnectionBase)Activator.CreateInstance(connType);
        }

        private static string SerializeToString(IFRSerializable serializable)
        {
            using var writer = new FRWriter();
            writer.SaveChildren = true;
            writer.WriteHeader = false;
            writer.Write(serializable);

            using var ms = new MemoryStream();
            writer.Save(ms);
            ms.Position = 0;

            using var reader = new StreamReader(ms, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private static void ApplyParameterToDataSource(TableDataSource dataSource, ParameterModel parameterModel)
        {
            var parameter = CreateParameter(parameterModel);
            dataSource.Parameters.Add(parameter);
        }

        private static bool IsConnectionStringValid(string connectionString, out string errorMsg)
        {
            if (string.IsNullOrEmpty(connectionString))
            {
                errorMsg = "Connection string is null or empty";
                return false;
            }

            errorMsg = string.Empty;
            return true;
        }

        public Dictionary<string, object> GetConnectionTypes(bool needSqlSupportInfo = false)
        {
            var result = new Dictionary<string, object>();
            var objects = new List<DataConnectionInfo>();
            RegisteredObjects.DataConnections.EnumItems(objects);

            foreach (var info in objects.Where(info => info.Object != null))
            {
                if (needSqlSupportInfo)
                {
                    using var conn = (DataConnectionBase)Activator.CreateInstance(info.Object);
                    result.Add(info.Object.FullName, GetConnectionJson(info.Text, conn.IsSqlBased));
                }
                else
                    result.Add(info.Object.FullName, Res.TryGetBuiltin(info.Text));
            }

            return result;
        }

        public Dictionary<string, int> GetParameterTypes(string connectionType, out string errorMsg)
        {
            var result = new Dictionary<string, int>();
            Type connType = GetConnectionType(connectionType);

            if (connType == null)
            {
                errorMsg = "Connection type not found";
                return result;
            }

            try
            {
                using (var conn = (DataConnectionBase)Activator.CreateInstance(connType))
                {
                    Array values;

                    var paramType = conn.GetParameterType();
                    if (paramType != null)
                        values = Enum.GetValues(paramType);
                    else 
                        values = Enum.GetValues<DbType>();

                    foreach (var par in values) 
                    {
                        result.Add(par.ToString(), (int)par);
                    }
                }
            }
            catch (Exception ex)
            {
                errorMsg = ex.ToString();
                return result;
            }

            errorMsg = "";
            return result;
        }

        private static string GetConnectionJson(string text, bool isSqlBased)
        {
            return $"{{\"connectionType\": \"{Res.TryGetBuiltin(text)}\", \"sqlSupport\": {isSqlBased.ToString().ToLowerInvariant()}}}";
        }

    }
}
