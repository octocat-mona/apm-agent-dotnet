// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AspNetFullFrameworkSampleApp.Controllers;
using Elastic.Apm.Api;
using Elastic.Apm.Model;
using Elastic.Apm.Tests.MockApmServer;
using Elastic.Apm.Tests.Utilities;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Elastic.Apm.AspNetFullFramework.Tests
{
	[Collection(Consts.AspNetFullFrameworkTestsCollection)]
	public class DbSpanTests : TestsBase
	{
		public DbSpanTests(ITestOutputHelper xUnitOutputHelper) : base(xUnitOutputHelper) { }

		/// <summary>
		/// <seealso cref="AspNetFullFrameworkSampleApp.Controllers.HomeController.SimpleDbTest" />
		/// </summary>
		[AspNetFullFrameworkFact]
		public async Task SimpleDbTest()
		{
			// 4 DB spans:
			// 		1) CREATE TABLE
			// 						the first "new SampleDataDbContext()"
			// 		2) INSERT for
			// 						dbCtx.Set<SampleData>().Add(new SampleData { Name = simpleDbTestSampleDataName });
			// 		3) SELECT for
			//						```if (dbCtx.Set<SampleData>().Count() != 1)
			// 		4) SELECT for
			// 						if (dbCtx.Set<SampleData>().First().Name != simpleDbTestSampleDataName)
			var pageData = new SampleAppUrlPathData(HomeController.SimpleDbTestPageRelativePath, 200, spansCount: 4);
			await SendGetRequestToSampleAppAndVerifyResponse(pageData.Uri, pageData.StatusCode);

			await WaitAndCustomVerifyReceivedData(receivedData =>
			{
				VerifyReceivedDataSharedConstraints(pageData, receivedData);

				// See comment for TestsBase.SampleAppUrlPaths.SimpleDbTestPage
				var dbStatements = new[] { "CREATE TABLE", "INSERT", "SELECT", "SELECT" };

				var transaction = receivedData.Transactions.First();

				receivedData.Spans.ForEachIndexed((span, i) =>
				{
					var signatureParser = new SignatureParser(new Scanner());
					var name = new StringBuilder();
					signatureParser.QuerySignature(dbStatements[i], name, preparedStatement: false);

					span.Name.Should().StartWith(name.ToString());
					span.Type.Should().Be(ApiConstants.TypeDb);
					span.Subtype.Should().Be(ApiConstants.SubtypeSqLite);
					span.Context.Db.Type.Should().Be(Database.TypeSql);
					span.Outcome.Should().Be(Outcome.Success);

					span.Context.Db.Instance.Should().NotBeNull();
					span.Context.Db.Instance.Should().Be(receivedData.Spans.First().Context.Db.Instance);

					span.Context.Db.Statement.Should().StartWith(dbStatements[i]);

					span.Context.Destination.Should().NotBeNull();

					span.TraceId.Should().Be(transaction.TraceId);
					span.TransactionId.Should().Be(transaction.Id);
					span.ParentId.Should().Be(transaction.Id);
					span.ShouldOccurBetween(transaction);

					if (i != 0)
						receivedData.Spans[i - 1].ShouldOccurBefore(span);
				});

				ShouldBeMonotonicInTime(receivedData.Spans);
			});
		}

		/// <summary>
		/// <seealso cref="AspNetFullFrameworkSampleApp.Controllers.HomeController.ConcurrentDbTest" />
		/// </summary>
		[AspNetFullFrameworkFact]
		public async Task ConcurrentDbTest()
		{
			const int numberOfConcurrentIterations = HomeController.ConcurrentDbTestNumberOfIterations;
			(numberOfConcurrentIterations % 2).Should()
				.Be(0
					, $"because numberOfConcurrentIterations should be even. numberOfConcurrentIterations: {numberOfConcurrentIterations}");
			// numberOfConcurrentIterations *3 + 5 DB spans:
			// 		1) CREATE TABLE
			// 		2) SELECT for
			// 						if (dbCtx.Set<SampleData>().Count() != 0)
			// 		3) 2 concurrent spans
			// 			3.1) numberOfConcurrentIterations * 3 INSERT-s
			// 		4) SELECT for
			// 						var sampleDataList = dbCtx.Set<SampleData>().ToList();
			//
			var pageData = new SampleAppUrlPathData(HomeController.ConcurrentDbTestPageRelativePath, 200
				, spansCount: numberOfConcurrentIterations * 3 + 5);
			await SendGetRequestToSampleAppAndVerifyResponse(pageData.Uri, pageData.StatusCode);

			await WaitAndCustomVerifyReceivedData(receivedData =>
			{
				VerifyReceivedDataSharedConstraints(pageData, receivedData);

				var transaction = receivedData.Transactions.First();

				// ReSharper disable PossibleMultipleEnumeration

				var topLevelSpans = receivedData.Spans.Where(span => span.ParentId == transaction.Id);
				topLevelSpans.Should().HaveCount(5);

				var topLevelDbSpans = topLevelSpans.Where(span => span.Type == ApiConstants.TypeDb);
				topLevelDbSpans.Should().HaveCount(3);
				ShouldBeMonotonicInTime(topLevelDbSpans);

				var topLevelConcurrentSpans = topLevelSpans.Where(span => span.Type == HomeController.ConcurrentDbTestSpanType).ToList();
				topLevelConcurrentSpans.Should().HaveCount(2);

				var allChildDbSpans = new List<SpanDto>[2];
				// ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
				foreach (var topLevelConcurrentSpan in topLevelConcurrentSpans)
				{
					var childDbSpans = receivedData.Spans.Where(span => span.ParentId == topLevelConcurrentSpan.Id);
					childDbSpans.Should().HaveCount(numberOfConcurrentIterations * 3 / 2);
					ShouldBeMonotonicInTime(childDbSpans);

					childDbSpans.ForEachIndexed((childDbSpan, i) =>
					{
						childDbSpan.ParentId.Should().Be(topLevelConcurrentSpan.Id);
						childDbSpan.ShouldOccurBetween(topLevelConcurrentSpan);
					});

					(topLevelConcurrentSpan.Name == "A" || topLevelConcurrentSpan.Name == "B").Should().BeTrue();
					var index = topLevelConcurrentSpan.Name == "A" ? 0 : 1;
					allChildDbSpans[index].Should().BeNull();
					allChildDbSpans[index] = childDbSpans.ToList();
				}

				var indexInBranch = new int[2];
				numberOfConcurrentIterations.Repeat(i =>
				{
					var containingSpanBranchIndex = i % 2 == 0 ? 0 : 1;
					var containedSpansBranchIndex = (containingSpanBranchIndex + 1) % 2;
					var containingSpan = allChildDbSpans[containingSpanBranchIndex][indexInBranch[containingSpanBranchIndex]];
					var containedSpanBefore = allChildDbSpans[containedSpansBranchIndex][indexInBranch[containedSpansBranchIndex]];
					var containedSpanAfter = allChildDbSpans[containedSpansBranchIndex][indexInBranch[containedSpansBranchIndex] + 1];
					(OccursBetween(containedSpanBefore, containingSpan) || OccursBetween(containedSpanAfter, containingSpan)).Should()
						.BeTrue(
							$"containingSpan: {containingSpan}, containedSpanBefore: {containedSpanBefore}, containedSpanAfter: {containedSpanAfter}");
					indexInBranch[containingSpanBranchIndex] += 1;
					indexInBranch[containedSpansBranchIndex] += 2;
				});

				var dbStatements = new List<string> { "CREATE TABLE", "SELECT", "SELECT" };
				var allChildDbSpansFlattened = allChildDbSpans.SelectMany(x => x);
				dbStatements.AddRange(Enumerable.Repeat("INSERT", allChildDbSpansFlattened.Count()));
				topLevelDbSpans.Concat(allChildDbSpansFlattened)
					.ForEachIndexed((dbSpan, i) =>
					{
						var signatureParser = new SignatureParser(new Scanner());
						var name = new StringBuilder();
						signatureParser.QuerySignature(dbStatements[i], name, preparedStatement: false);

						dbSpan.Name.Should().StartWith(name.ToString());
						dbSpan.Type.Should().Be(ApiConstants.TypeDb);
						dbSpan.Subtype.Should().Be(ApiConstants.SubtypeSqLite);
						dbSpan.Context.Db.Type.Should().Be(Database.TypeSql);

						dbSpan.Context.Db.Instance.Should().NotBeNull();
						dbSpan.Context.Db.Instance.Should().Be(topLevelDbSpans.First().Context.Db.Instance);

						dbSpan.Context.Db.Statement.Should().StartWith(dbStatements[i]);

						dbSpan.Context.Destination.Should().NotBeNull();

						dbSpan.TraceId.Should().Be(transaction.TraceId);
						dbSpan.TransactionId.Should().Be(transaction.Id);
						dbSpan.ShouldOccurBetween(transaction);
					});

				// ReSharper restore PossibleMultipleEnumeration
			});
		}

		/// <summary>
		/// <seealso cref="AspNetFullFrameworkSampleApp.Controllers.HomeController.DbOperationOutsideTransactionTest" />
		/// </summary>
		[AspNetFullFrameworkFact]
		public async Task DbOperationOutsideTransactionTest()
		{
			var pageData = new SampleAppUrlPathData(HomeController.DbOperationOutsideTransactionTestPageRelativePath
				, HomeController.DbOperationOutsideTransactionTestStatusCode);

			await SendGetRequestToSampleAppAndVerifyResponse(pageData.Uri, pageData.StatusCode);
			await WaitAndVerifyReceivedDataSharedConstraints(pageData);
		}

		/// <summary>
		/// The test app generates a failing DB call and this test makes sure the outcome is set correctly to Failure
		/// </summary>
		[AspNetFullFrameworkFact]
		public async Task FailingDbCallTest()
		{
			var pageData = new SampleAppUrlPathData(HomeController.FailingDbCallTestPageRelativePath
				, HomeController.FailingDbCallTestStatusCode);

			await SendGetRequestToSampleAppAndVerifyResponse(pageData.Uri, pageData.StatusCode);

			await WaitAndCustomVerifyReceivedData(receivedData =>
			{
				receivedData.Transactions.Should().NotBeNullOrEmpty();
				receivedData.Spans.Should().NotBeNullOrEmpty();

				var selectSpan = receivedData.Spans
					.First(n => n.Context.Db != null && n.Context.Db.Statement.ToLower().Contains("select * from nonexistingtable"));
				selectSpan.Should().NotBeNull();
				selectSpan.Outcome.Should().Be(Outcome.Failure);
			});
		}
	}
}
