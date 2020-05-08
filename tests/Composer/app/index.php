<?php

namespace User\App;

use DDTrace\Configuration;
use DDTrace\GlobalTracer;
use DDTrace\Log\ErrorLogLogger;
use DDTrace\Log\Logger;
use DDTrace\Tag;
use DDTrace\Type;

$scenario = getenv('DD_COMPOSER_TEST_CONTEXT');

require __DIR__ . "/vendor/autoload.php";

/*
 * Manual instrumentation using DD api
 */
$tracer = GlobalTracer::get();
$scope = $tracer->startActiveSpan('my_operation');
$span = $scope->getSpan();
$span->setResource('my_resource');
// A tag from composer namespace
$span->setTag(Tag::HTTP_METHOD, 'GET');
// A type from composer namespace
$span->setTag(Tag::SPAN_TYPE, Type::MEMCACHED);
$scope->close();

/*
 * Using Configuration class from 'api' (which no longer exists in the 'src')
 */
Configuration::get()->appName('default');

/*
 * Using Logger class which is defined in both 'src' AND 'api'
 */
Logger::set(new ErrorLogLogger('debug'));
Logger::get()->debug('some-debug-message');

echo "OK\n";
