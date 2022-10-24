﻿using Chloe.Annotations;
using Chloe.Core.Visitors;
using Chloe.DbExpressions;
using Chloe.InternalExtensions;
using Chloe.RDBMS;
using Chloe.Reflection;
using System.Data;
using System.Reflection;

namespace Chloe.SQLite
{
    partial class SqlGenerator : SqlGeneratorBase
    {
        DbParamCollection _parameters = new DbParamCollection();

        public static readonly Dictionary<string, IMethodHandler> MethodHandlers = GetMethodHandlers();
        static readonly Dictionary<string, Action<DbAggregateExpression, SqlGeneratorBase>> AggregateHandlers = InitAggregateHandlers();
        static readonly Dictionary<MethodInfo, Action<DbBinaryExpression, SqlGeneratorBase>> BinaryWithMethodHandlers = InitBinaryWithMethodHandlers();
        public static readonly Dictionary<Type, string> CastTypeMap;
        public static readonly Dictionary<Type, Type> NumericTypes;
        static readonly List<string> CacheParameterNames;

        static SqlGenerator()
        {
            Dictionary<Type, string> castTypeMap = new Dictionary<Type, string>();
            castTypeMap.Add(typeof(string), "TEXT");
            castTypeMap.Add(typeof(byte), "INTEGER");
            castTypeMap.Add(typeof(Int16), "INTEGER");
            castTypeMap.Add(typeof(int), "INTEGER");
            castTypeMap.Add(typeof(long), "INTEGER");
            castTypeMap.Add(typeof(float), "REAL");
            castTypeMap.Add(typeof(double), "REAL");
            //castTypeMap.Add(typeof(decimal), "DECIMAL(19,0)");//I think this will be a bug.
            castTypeMap.Add(typeof(bool), "INTEGER");
            CastTypeMap = PublicHelper.Clone(castTypeMap);


            Dictionary<Type, Type> numericTypes = new Dictionary<Type, Type>();
            numericTypes.Add(typeof(byte), typeof(byte));
            numericTypes.Add(typeof(sbyte), typeof(sbyte));
            numericTypes.Add(typeof(short), typeof(short));
            numericTypes.Add(typeof(ushort), typeof(ushort));
            numericTypes.Add(typeof(int), typeof(int));
            numericTypes.Add(typeof(uint), typeof(uint));
            numericTypes.Add(typeof(long), typeof(long));
            numericTypes.Add(typeof(ulong), typeof(ulong));
            numericTypes.Add(typeof(float), typeof(float));
            numericTypes.Add(typeof(double), typeof(double));
            numericTypes.Add(typeof(decimal), typeof(decimal));
            NumericTypes = PublicHelper.Clone(numericTypes);


            int cacheParameterNameCount = 2 * 12;
            List<string> cacheParameterNames = new List<string>(cacheParameterNameCount);
            for (int i = 0; i < cacheParameterNameCount; i++)
            {
                string paramName = UtilConstants.ParameterNamePrefix + i.ToString();
                cacheParameterNames.Add(paramName);
            }
            CacheParameterNames = cacheParameterNames;
        }

        public List<DbParam> Parameters { get { return this._parameters.ToParameterList(); } }

        public static SqlGenerator CreateInstance()
        {
            return new SqlGenerator();
        }

        public override DbExpression Visit(DbEqualExpression exp)
        {
            DbExpression left = exp.Left;
            DbExpression right = exp.Right;

            left = DbExpressionExtension.StripInvalidConvert(left);
            right = DbExpressionExtension.StripInvalidConvert(right);

            MethodInfo method_Sql_IsEqual = PublicConstants.MethodInfo_Sql_IsEqual.MakeGenericMethod(left.Type);

            /* Sql.IsEqual(left, right) */
            DbMethodCallExpression left_equals_right = DbExpression.MethodCall(null, method_Sql_IsEqual, new List<DbExpression>(2) { left, right });

            if (right.NodeType == DbExpressionType.Parameter || right.NodeType == DbExpressionType.Constant || left.NodeType == DbExpressionType.Parameter || left.NodeType == DbExpressionType.Constant || right.NodeType == DbExpressionType.SubQuery || left.NodeType == DbExpressionType.SubQuery || !left.Type.CanNull() || !right.Type.CanNull())
            {
                /*
                 * a.Name == name --> a.Name == name
                 * a.Id == (select top 1 T.Id from T) --> a.Id == (select top 1 T.Id from T)
                 * 对于上述查询，我们不考虑 null
                 */

                left_equals_right.Accept(this);
                return exp;
            }


            /*
             * a.Name == a.XName --> a.Name == a.XName or (a.Name is null and a.XName is null)
             */

            /* Sql.IsEqual(left, null) */
            var left_is_null = DbExpression.MethodCall(null, method_Sql_IsEqual, new List<DbExpression>(2) { left, DbExpression.Constant(null, left.Type) });

            /* Sql.IsEqual(right, null) */
            var right_is_null = DbExpression.MethodCall(null, method_Sql_IsEqual, new List<DbExpression>(2) { right, DbExpression.Constant(null, right.Type) });

            /* Sql.IsEqual(left, null) && Sql.IsEqual(right, null) */
            var left_is_null_and_right_is_null = DbExpression.And(left_is_null, right_is_null);

            /* Sql.IsEqual(left, right) || (Sql.IsEqual(left, null) && Sql.IsEqual(right, null)) */
            var left_equals_right_or_left_is_null_and_right_is_null = DbExpression.Or(left_equals_right, left_is_null_and_right_is_null);

            left_equals_right_or_left_is_null_and_right_is_null.Accept(this);

            return exp;
        }
        public override DbExpression Visit(DbNotEqualExpression exp)
        {
            DbExpression left = exp.Left;
            DbExpression right = exp.Right;

            left = DbExpressionExtension.StripInvalidConvert(left);
            right = DbExpressionExtension.StripInvalidConvert(right);

            MethodInfo method_Sql_IsNotEqual = PublicConstants.MethodInfo_Sql_IsNotEqual.MakeGenericMethod(left.Type);

            /* Sql.IsNotEqual(left, right) */
            DbMethodCallExpression left_not_equals_right = DbExpression.MethodCall(null, method_Sql_IsNotEqual, new List<DbExpression>(2) { left, right });

            //明确 left right 其中一边一定为 null
            if (DbExpressionExtension.AffirmExpressionRetValueIsNull(right) || DbExpressionExtension.AffirmExpressionRetValueIsNull(left))
            {
                /*
                 * a.Name != null --> a.Name != null
                 */

                left_not_equals_right.Accept(this);
                return exp;
            }

            if (right.NodeType == DbExpressionType.SubQuery || left.NodeType == DbExpressionType.SubQuery)
            {
                /*
                 * a.Id != (select top 1 T.Id from T) --> a.Id <> (select top 1 T.Id from T)，对于这种查询，我们不考虑 null
                 */

                left_not_equals_right.Accept(this);
                return exp;
            }

            MethodInfo method_Sql_IsEqual = PublicConstants.MethodInfo_Sql_IsEqual.MakeGenericMethod(left.Type);

            if (left.NodeType == DbExpressionType.Parameter || left.NodeType == DbExpressionType.Constant)
            {
                var t = right;
                right = left;
                left = t;
            }
            if (right.NodeType == DbExpressionType.Parameter || right.NodeType == DbExpressionType.Constant)
            {
                /*
                 * 走到这说明 name 不可能为 null
                 * a.Name != name --> a.Name <> name or a.Name is null
                 */

                if (left.NodeType != DbExpressionType.Parameter && left.NodeType != DbExpressionType.Constant)
                {
                    /*
                     * a.Name != name --> a.Name <> name or a.Name is null
                     */

                    /* Sql.IsEqual(left, null) */
                    var left_is_null1 = DbExpression.MethodCall(null, method_Sql_IsEqual, new List<DbExpression>(2) { left, DbExpression.Constant(null, left.Type) });

                    /* Sql.IsNotEqual(left, right) || Sql.IsEqual(left, null) */
                    var left_not_equals_right_or_left_is_null = DbExpression.Or(left_not_equals_right, left_is_null1);
                    left_not_equals_right_or_left_is_null.Accept(this);
                }
                else
                {
                    /*
                     * name != name1 --> name <> name，其中 name 和 name1 都为变量且都不可能为 null
                     */

                    left_not_equals_right.Accept(this);
                }

                return exp;
            }


            /*
             * a.Name != a.XName --> a.Name <> a.XName or (a.Name is null and a.XName is not null) or (a.Name is not null and a.XName is null)
             * ## a.Name != a.XName 不能翻译成：not (a.Name == a.XName or (a.Name is null and a.XName is null))，因为数据库里的 not 有时候并非真正意义上的“取反”！
             * 当 a.Name 或者 a.XName 其中一个字段有为 NULL，另一个字段有值时，会查不出此条数据 ##
             */

            DbConstantExpression null_Constant = DbExpression.Constant(null, left.Type);

            /* Sql.IsEqual(left, null) */
            var left_is_null = DbExpression.MethodCall(null, method_Sql_IsEqual, new List<DbExpression>(2) { left, null_Constant });
            /* Sql.IsNotEqual(left, null) */
            var left_is_not_null = DbExpression.MethodCall(null, method_Sql_IsNotEqual, new List<DbExpression>(2) { left, null_Constant });

            /* Sql.IsEqual(right, null) */
            var right_is_null = DbExpression.MethodCall(null, method_Sql_IsEqual, new List<DbExpression>(2) { right, null_Constant });
            /* Sql.IsNotEqual(right, null) */
            var right_is_not_null = DbExpression.MethodCall(null, method_Sql_IsNotEqual, new List<DbExpression>(2) { right, null_Constant });

            /* Sql.IsEqual(left, null) && Sql.IsNotEqual(right, null) */
            var left_is_null_and_right_is_not_null = DbExpression.And(left_is_null, right_is_not_null);

            /* Sql.IsNotEqual(left, null) && Sql.IsEqual(right, null) */
            var left_is_not_null_and_right_is_null = DbExpression.And(left_is_not_null, right_is_null);

            /* (Sql.IsEqual(left, null) && Sql.IsNotEqual(right, null)) || (Sql.IsNotEqual(left, null) && Sql.IsEqual(right, null)) */
            var left_is_null_and_right_is_not_null_or_left_is_not_null_and_right_is_null = DbExpression.Or(left_is_null_and_right_is_not_null, left_is_not_null_and_right_is_null);

            /* Sql.IsNotEqual(left, right) || (Sql.IsEqual(left, null) && Sql.IsNotEqual(right, null)) || (Sql.IsNotEqual(left, null) && Sql.IsEqual(right, null)) */
            var e = DbExpression.Or(left_not_equals_right, left_is_null_and_right_is_not_null_or_left_is_not_null_and_right_is_null);

            e.Accept(this);

            return exp;
        }

        public override DbExpression Visit(DbNotExpression exp)
        {
            this.SqlBuilder.Append("NOT ");
            this.SqlBuilder.Append("(");
            exp.Operand.Accept(this);
            this.SqlBuilder.Append(")");

            return exp;
        }

        public override DbExpression Visit(DbBitAndExpression exp)
        {
            Stack<DbExpression> operands = GatherBinaryExpressionOperand(exp);
            this.ConcatOperands(operands, " & ");

            return exp;
        }
        public override DbExpression Visit(DbAndExpression exp)
        {
            Stack<DbExpression> operands = GatherBinaryExpressionOperand(exp);
            this.ConcatOperands(operands, " AND ");

            return exp;
        }
        public override DbExpression Visit(DbBitOrExpression exp)
        {
            Stack<DbExpression> operands = GatherBinaryExpressionOperand(exp);
            this.ConcatOperands(operands, " | ");

            return exp;
        }
        public override DbExpression Visit(DbOrExpression exp)
        {
            Stack<DbExpression> operands = GatherBinaryExpressionOperand(exp);
            this.ConcatOperands(operands, " OR ");

            return exp;
        }

        // +
        public override DbExpression Visit(DbAddExpression exp)
        {
            MethodInfo method = exp.Method;
            if (method != null)
            {
                Action<DbBinaryExpression, SqlGeneratorBase> handler;
                if (BinaryWithMethodHandlers.TryGetValue(method, out handler))
                {
                    handler(exp, this);
                    return exp;
                }
            }

            Stack<DbExpression> operands = GatherBinaryExpressionOperand(exp);
            this.ConcatOperands(operands, " + ");

            return exp;
        }
        // -
        public override DbExpression Visit(DbSubtractExpression exp)
        {
            Stack<DbExpression> operands = GatherBinaryExpressionOperand(exp);
            this.ConcatOperands(operands, " - ");

            return exp;
        }
        // *
        public override DbExpression Visit(DbMultiplyExpression exp)
        {
            Stack<DbExpression> operands = GatherBinaryExpressionOperand(exp);
            this.ConcatOperands(operands, " * ");

            return exp;
        }
        // /
        public override DbExpression Visit(DbDivideExpression exp)
        {
            Stack<DbExpression> operands = GatherBinaryExpressionOperand(exp);
            this.ConcatOperands(operands, " / ");

            return exp;
        }
        // %
        public override DbExpression Visit(DbModuloExpression exp)
        {
            Stack<DbExpression> operands = GatherBinaryExpressionOperand(exp);
            this.ConcatOperands(operands, " % ");

            return exp;
        }
        public override DbExpression Visit(DbNegateExpression exp)
        {
            this.SqlBuilder.Append("(");

            this.SqlBuilder.Append("-");
            exp.Operand.Accept(this);

            this.SqlBuilder.Append(")");
            return exp;
        }
        // <
        public override DbExpression Visit(DbLessThanExpression exp)
        {
            var amendResult = AmendExpDbInfo(exp.Left, exp.Right);

            amendResult.Left.Accept(this);
            this.SqlBuilder.Append(" < ");
            amendResult.Right.Accept(this);

            return exp;
        }
        // <=
        public override DbExpression Visit(DbLessThanOrEqualExpression exp)
        {
            var amendResult = AmendExpDbInfo(exp.Left, exp.Right);

            amendResult.Left.Accept(this);
            this.SqlBuilder.Append(" <= ");
            amendResult.Right.Accept(this);

            return exp;
        }
        // >
        public override DbExpression Visit(DbGreaterThanExpression exp)
        {
            var amendResult = AmendExpDbInfo(exp.Left, exp.Right);

            amendResult.Left.Accept(this);
            this.SqlBuilder.Append(" > ");
            amendResult.Right.Accept(this);

            return exp;
        }
        // >=
        public override DbExpression Visit(DbGreaterThanOrEqualExpression exp)
        {
            var amendResult = AmendExpDbInfo(exp.Left, exp.Right);

            amendResult.Left.Accept(this);
            this.SqlBuilder.Append(" >= ");
            amendResult.Right.Accept(this);

            return exp;
        }


        public override DbExpression Visit(DbAggregateExpression exp)
        {
            Action<DbAggregateExpression, SqlGeneratorBase> aggregateHandler;
            if (!AggregateHandlers.TryGetValue(exp.Method.Name, out aggregateHandler))
            {
                throw UtilExceptions.NotSupportedMethod(exp.Method);
            }

            aggregateHandler(exp, this);
            return exp;
        }


        public override DbExpression Visit(DbTableExpression exp)
        {
            this.AppendTable(exp.Table);
            return exp;
        }
        public override DbExpression Visit(DbColumnAccessExpression exp)
        {
            this.QuoteName(exp.Table.Name);
            this.SqlBuilder.Append(".");
            this.QuoteName(exp.Column.Name);

            return exp;
        }
        public override DbExpression Visit(DbFromTableExpression exp)
        {
            this.AppendTableSegment(exp.Table);
            this.VisitDbJoinTableExpressions(exp.JoinTables);

            return exp;
        }
        public override DbExpression Visit(DbJoinTableExpression exp)
        {
            DbJoinTableExpression joinTablePart = exp;
            string joinString = null;

            if (joinTablePart.JoinType == DbJoinType.InnerJoin)
            {
                joinString = " INNER JOIN ";
            }
            else if (joinTablePart.JoinType == DbJoinType.LeftJoin)
            {
                joinString = " LEFT JOIN ";
            }
            else
                throw new NotSupportedException("JoinType: " + joinTablePart.JoinType);

            this.SqlBuilder.Append(joinString);
            this.AppendTableSegment(joinTablePart.Table);
            this.SqlBuilder.Append(" ON ");
            JoinConditionExpressionTransformer.Transform(joinTablePart.Condition).Accept(this);
            this.VisitDbJoinTableExpressions(joinTablePart.JoinTables);

            return exp;
        }


        public override DbExpression Visit(DbSubQueryExpression exp)
        {
            this.SqlBuilder.Append("(");
            exp.SqlQuery.Accept(this);
            this.SqlBuilder.Append(")");

            return exp;
        }
        public override DbExpression Visit(DbSqlQueryExpression exp)
        {
            this.BuildGeneralSql(exp);
            return exp;
        }
        public override DbExpression Visit(DbInsertExpression exp)
        {
            this.SqlBuilder.Append("INSERT INTO ");
            this.AppendTable(exp.Table);
            this.SqlBuilder.Append("(");

            bool first = true;
            foreach (var item in exp.InsertColumns)
            {
                if (first)
                    first = false;
                else
                {
                    this.SqlBuilder.Append(",");
                }

                this.QuoteName(item.Key.Name);
            }

            this.SqlBuilder.Append(")");

            this.SqlBuilder.Append(" VALUES(");
            first = true;
            foreach (var item in exp.InsertColumns)
            {
                if (first)
                    first = false;
                else
                {
                    this.SqlBuilder.Append(",");
                }

                DbExpression valExp = item.Value.StripInvalidConvert();
                AmendDbInfo(item.Key, valExp);
                valExp.Accept(this);
            }

            this.SqlBuilder.Append(")");

            return exp;
        }
        public override DbExpression Visit(DbUpdateExpression exp)
        {
            this.SqlBuilder.Append("UPDATE ");
            this.AppendTable(exp.Table);
            this.SqlBuilder.Append(" SET ");

            bool first = true;
            foreach (var item in exp.UpdateColumns)
            {
                if (first)
                    first = false;
                else
                    this.SqlBuilder.Append(",");

                this.QuoteName(item.Key.Name);
                this.SqlBuilder.Append("=");

                DbExpression valExp = item.Value.StripInvalidConvert();
                AmendDbInfo(item.Key, valExp);
                valExp.Accept(this);
            }

            this.BuildWhereState(exp.Condition);

            return exp;
        }
        public override DbExpression Visit(DbDeleteExpression exp)
        {
            this.SqlBuilder.Append("DELETE FROM ");
            this.AppendTable(exp.Table);
            this.BuildWhereState(exp.Condition);

            return exp;
        }

        public override DbExpression Visit(DbExistsExpression exp)
        {
            this.SqlBuilder.Append("Exists ");

            DbSqlQueryExpression rawSqlQuery = exp.SqlQuery;
            DbSqlQueryExpression sqlQuery = new DbSqlQueryExpression()
            {
                TakeCount = rawSqlQuery.TakeCount,
                SkipCount = rawSqlQuery.SkipCount,
                Table = rawSqlQuery.Table,
                Condition = rawSqlQuery.Condition,
                HavingCondition = rawSqlQuery.HavingCondition,
            };

            sqlQuery.GroupSegments.AddRange(rawSqlQuery.GroupSegments);

            DbColumnSegment columnSegment = new DbColumnSegment(DbExpression.Parameter("1"), "C");
            sqlQuery.ColumnSegments.Add(columnSegment);

            DbSubQueryExpression subQuery = new DbSubQueryExpression(sqlQuery);
            return subQuery.Accept(this);
        }

        public override DbExpression Visit(DbCoalesceExpression exp)
        {
            this.SqlBuilder.Append("IFNULL(");
            exp.CheckExpression.Accept(this);
            this.SqlBuilder.Append(",");
            exp.ReplacementValue.Accept(this);
            this.SqlBuilder.Append(")");

            return exp;
        }
        public override DbExpression Visit(DbCaseWhenExpression exp)
        {
            this.SqlBuilder.Append("CASE");
            foreach (var whenThen in exp.WhenThenPairs)
            {
                this.SqlBuilder.Append(" WHEN ");
                whenThen.When.Accept(this);
                this.SqlBuilder.Append(" THEN ");
                whenThen.Then.Accept(this);
            }

            this.SqlBuilder.Append(" ELSE ");
            exp.Else.Accept(this);
            this.SqlBuilder.Append(" END");

            return exp;
        }
        public override DbExpression Visit(DbConvertExpression exp)
        {
            DbExpression stripedExp = DbExpressionExtension.StripInvalidConvert(exp);

            if (stripedExp.NodeType != DbExpressionType.Convert)
            {
                stripedExp.Accept(this);
                return exp;
            }

            exp = (DbConvertExpression)stripedExp;

            string dbTypeString;
            if (TryGetCastTargetDbTypeString(exp.Operand.Type, exp.Type, out dbTypeString, false))
            {
                this.BuildCastState(exp.Operand, dbTypeString);
            }
            else
            {
                Type targetType = ReflectionExtension.GetUnderlyingType(exp.Type);
                if (targetType == PublicConstants.TypeOfDateTime)
                {
                    /* DATETIME('2016-08-06 09:01:24') */
                    this.SqlBuilder.Append("DATETIME(");
                    exp.Operand.Accept(this);
                    this.SqlBuilder.Append(")");
                }
                else
                    exp.Operand.Accept(this);
            }

            return exp;
        }


        public override DbExpression Visit(DbMethodCallExpression exp)
        {
            IMethodHandler methodHandler;
            if (MethodHandlers.TryGetValue(exp.Method.Name, out methodHandler))
            {
                if (methodHandler.CanProcess(exp))
                {
                    methodHandler.Process(exp, this);
                    return exp;
                }
            }

            DbFunctionAttribute dbFunction = exp.Method.GetCustomAttribute<DbFunctionAttribute>();
            if (dbFunction != null)
            {
                string functionName = string.IsNullOrEmpty(dbFunction.Name) ? exp.Method.Name : dbFunction.Name;

                this.QuoteName(functionName);
                this.SqlBuilder.Append("(");

                string c = "";
                foreach (DbExpression argument in exp.Arguments)
                {
                    this.SqlBuilder.Append(c);
                    argument.Accept(this);
                    c = ",";
                }

                this.SqlBuilder.Append(")");

                return exp;
            }

            if (exp.IsEvaluable())
            {
                DbParameterExpression dbParameter = new DbParameterExpression(exp.Evaluate(), exp.Type);
                return dbParameter.Accept(this);
            }

            throw UtilExceptions.NotSupportedMethod(exp.Method);
        }
        public override DbExpression Visit(DbMemberExpression exp)
        {
            MemberInfo member = exp.Member;

            if (member.DeclaringType == PublicConstants.TypeOfDateTime)
            {
                if (member == PublicConstants.PropertyInfo_DateTime_Now)
                {
                    this.SqlBuilder.Append("DATETIME('NOW','LOCALTIME')");
                    return exp;
                }

                if (member == PublicConstants.PropertyInfo_DateTime_UtcNow)
                {
                    this.SqlBuilder.Append("DATETIME()");
                    return exp;
                }

                if (member == PublicConstants.PropertyInfo_DateTime_Today)
                {
                    this.SqlBuilder.Append("DATE('NOW','LOCALTIME')");
                    return exp;
                }

                if (member == PublicConstants.PropertyInfo_DateTime_Date)
                {
                    this.SqlBuilder.Append("DATETIME(DATE(");
                    exp.Expression.Accept(this);
                    this.SqlBuilder.Append("))");
                    return exp;
                }

                if (this.IsDatePart(exp))
                {
                    return exp;
                }
            }

            if (member.Name == "Length" && member.DeclaringType == PublicConstants.TypeOfString)
            {
                this.SqlBuilder.Append("LENGTH(");
                exp.Expression.Accept(this);
                this.SqlBuilder.Append(")");

                return exp;
            }

            if (member.Name == "Value" && ReflectionExtension.IsNullable(exp.Expression.Type))
            {
                exp.Expression.Accept(this);
                return exp;
            }

            DbParameterExpression newExp;
            if (DbExpressionExtension.TryConvertToParameterExpression(exp, out newExp))
            {
                return newExp.Accept(this);
            }

            throw new NotSupportedException(string.Format("'{0}.{1}' is not supported.", member.DeclaringType.FullName, member.Name));
        }
        public override DbExpression Visit(DbConstantExpression exp)
        {
            if (exp.Value == null || exp.Value == DBNull.Value)
            {
                this.SqlBuilder.Append("NULL");
                return exp;
            }

            var objType = exp.Value.GetType();
            if (objType == PublicConstants.TypeOfBoolean)
            {
                this.SqlBuilder.Append(((bool)exp.Value) ? "1" : "0");
                return exp;
            }
            else if (objType == PublicConstants.TypeOfString)
            {
                this.SqlBuilder.Append("'", exp.Value, "'");
                return exp;
            }
            else if (objType.IsEnum)
            {
                this.SqlBuilder.Append(Convert.ChangeType(exp.Value, Enum.GetUnderlyingType(objType)).ToString());
                return exp;
            }
            else if (NumericTypes.ContainsKey(exp.Value.GetType()))
            {
                this.SqlBuilder.Append(exp.Value);
                return exp;
            }

            DbParameterExpression p = new DbParameterExpression(exp.Value);
            p.Accept(this);

            return exp;
        }
        public override DbExpression Visit(DbParameterExpression exp)
        {
            object paramValue = exp.Value;
            Type paramType = exp.Type.GetUnderlyingType();

            if (paramType.IsEnum)
            {
                paramType = Enum.GetUnderlyingType(paramType);
                if (paramValue != null)
                    paramValue = Convert.ChangeType(paramValue, paramType);
            }

            if (paramValue == null)
                paramValue = DBNull.Value;

            DbParam p = this._parameters.Find(paramValue, paramType, exp.DbType);

            if (p != null)
            {
                this.SqlBuilder.Append(p.Name);
                return exp;
            }

            string paramName = GenParameterName(this._parameters.Count);
            p = DbParam.Create(paramName, paramValue, paramType);

            if (paramValue.GetType() == PublicConstants.TypeOfString)
            {
                if (exp.DbType == DbType.AnsiStringFixedLength || exp.DbType == DbType.StringFixedLength)
                    p.Size = ((string)paramValue).Length;
                else if (((string)paramValue).Length <= 4000)
                    p.Size = 4000;
            }

            if (exp.DbType != null)
                p.DbType = exp.DbType;

            this._parameters.Add(p);
            this.SqlBuilder.Append(paramName);
            return exp;
        }


        void AppendTableSegment(DbTableSegment seg)
        {
            seg.Body.Accept(this);
            this.SqlBuilder.Append(" AS ");
            this.QuoteName(seg.Alias);
        }
        internal void AppendColumnSegment(DbColumnSegment seg)
        {
            seg.Body.Accept(this);
            this.SqlBuilder.Append(" AS ");
            this.QuoteName(seg.Alias);
        }
        void AppendOrdering(DbOrdering ordering)
        {
            if (ordering.OrderType == DbOrderType.Asc)
            {
                ordering.Expression.Accept(this);
                this.SqlBuilder.Append(" ASC");
                return;
            }
            else if (ordering.OrderType == DbOrderType.Desc)
            {
                ordering.Expression.Accept(this);
                this.SqlBuilder.Append(" DESC");
                return;
            }

            throw new NotSupportedException("OrderType: " + ordering.OrderType);
        }

        void VisitDbJoinTableExpressions(List<DbJoinTableExpression> tables)
        {
            foreach (var table in tables)
            {
                table.Accept(this);
            }
        }
        void BuildGeneralSql(DbSqlQueryExpression exp)
        {
            this.SqlBuilder.Append("SELECT ");

            if (exp.IsDistinct)
                this.SqlBuilder.Append("DISTINCT ");

            List<DbColumnSegment> columns = exp.ColumnSegments;
            for (int i = 0; i < columns.Count; i++)
            {
                DbColumnSegment column = columns[i];
                if (i > 0)
                    this.SqlBuilder.Append(",");

                this.AppendColumnSegment(column);
            }

            this.SqlBuilder.Append(" FROM ");
            exp.Table.Accept(this);
            this.BuildWhereState(exp.Condition);
            this.BuildGroupState(exp);
            this.BuildOrderState(exp.Orderings);

            if (exp.SkipCount == null && exp.TakeCount == null)
                return;

            int skipCount = exp.SkipCount ?? 0;
            long takeCount = long.MaxValue;
            if (exp.TakeCount != null)
                takeCount = exp.TakeCount.Value;

            this.SqlBuilder.Append(" LIMIT ", takeCount.ToString(), " OFFSET ", skipCount.ToString());
        }

        void BuildWhereState(DbExpression whereExpression)
        {
            if (whereExpression != null)
            {
                this.SqlBuilder.Append(" WHERE ");
                whereExpression.Accept(this);
            }
        }
        void BuildOrderState(List<DbOrdering> orderings)
        {
            if (orderings.Count > 0)
            {
                this.SqlBuilder.Append(" ORDER BY ");
                this.ConcatOrderings(orderings);
            }
        }
        void ConcatOrderings(List<DbOrdering> orderings)
        {
            for (int i = 0; i < orderings.Count; i++)
            {
                if (i > 0)
                {
                    this.SqlBuilder.Append(",");
                }

                this.AppendOrdering(orderings[i]);
            }
        }
        void BuildGroupState(DbSqlQueryExpression exp)
        {
            var groupSegments = exp.GroupSegments;
            if (groupSegments.Count == 0)
                return;

            this.SqlBuilder.Append(" GROUP BY ");
            for (int i = 0; i < groupSegments.Count; i++)
            {
                if (i > 0)
                    this.SqlBuilder.Append(",");

                groupSegments[i].Accept(this);
            }

            if (exp.HavingCondition != null)
            {
                this.SqlBuilder.Append(" HAVING ");
                exp.HavingCondition.Accept(this);
            }
        }

        void ConcatOperands(IEnumerable<DbExpression> operands, string connector)
        {
            this.SqlBuilder.Append("(");

            bool first = true;
            foreach (DbExpression operand in operands)
            {
                if (first)
                    first = false;
                else
                    this.SqlBuilder.Append(connector);

                operand.Accept(this);
            }

            this.SqlBuilder.Append(")");
            return;
        }

        void QuoteName(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("name");

            this.SqlBuilder.Append("[", name, "]");
        }
        void AppendTable(DbTable table)
        {
            this.QuoteName(table.Name);
        }

        void BuildCastState(DbExpression castExp, string targetDbTypeString)
        {
            this.SqlBuilder.Append("CAST(");
            castExp.Accept(this);
            this.SqlBuilder.Append(" AS ", targetDbTypeString, ")");
        }

        bool IsDatePart(DbMemberExpression exp)
        {
            MemberInfo member = exp.Member;

            if (member == PublicConstants.PropertyInfo_DateTime_Year)
            {
                DbFunction_DATEPART(this, "Y", exp.Expression);
                return true;
            }

            if (member == PublicConstants.PropertyInfo_DateTime_Month)
            {
                DbFunction_DATEPART(this, "m", exp.Expression);
                return true;
            }

            if (member == PublicConstants.PropertyInfo_DateTime_Day)
            {
                DbFunction_DATEPART(this, "d", exp.Expression);
                return true;
            }

            if (member == PublicConstants.PropertyInfo_DateTime_Hour)
            {
                DbFunction_DATEPART(this, "H", exp.Expression);
                return true;
            }

            if (member == PublicConstants.PropertyInfo_DateTime_Minute)
            {
                DbFunction_DATEPART(this, "M", exp.Expression);
                return true;
            }

            if (member == PublicConstants.PropertyInfo_DateTime_Second)
            {
                DbFunction_DATEPART(this, "S", exp.Expression);
                return true;
            }

            /* SQLite is not supports MILLISECOND */


            if (member == PublicConstants.PropertyInfo_DateTime_DayOfWeek)
            {
                DbFunction_DATEPART(this, "w", exp.Expression);
                return true;
            }

            return false;
        }
    }
}
